//go:build windows

package main

import (
	"bytes"
	"encoding/binary"
	"encoding/csv"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

const (
	routerVersion         = "2.0.2"
	windivertLayerNetwork = 0
	divertBufSize         = 0xFFFF
	protoTCP              = 6
	protoUDP              = 17
	defaultNATTTL         = 10 * time.Minute

	afInet              = 2
	tcpTableOwnerPidAll = 5
	udpTableOwnerPid    = 1
)

type Config struct {
	ClientIP         string
	AssignedIPFile   string
	VirtualCIDR      string
	Processes        string
	LogFile          string
	WinDivertDir     string
	NATTTL           time.Duration
	Priority         int
	InterfaceIndex   uint
	SessionFile      string
	SessionID        string
	LauncherPID      uint
	HeartbeatTimeout time.Duration
}

type IPv4 [4]byte

func (ip IPv4) String() string { return net.IPv4(ip[0], ip[1], ip[2], ip[3]).String() }
func parseIPv4(s string) (IPv4, error) {
	parsed := net.ParseIP(strings.TrimSpace(s)).To4()
	if parsed == nil {
		return IPv4{}, fmt.Errorf("invalid IPv4 address: %s", s)
	}
	var out IPv4
	copy(out[:], parsed)
	return out, nil
}
func ipv4FromPacket(b []byte) IPv4           { return IPv4{b[0], b[1], b[2], b[3]} }
func (ip IPv4) NetIP() net.IP                { return net.IPv4(ip[0], ip[1], ip[2], ip[3]) }
func ipInNet(ip IPv4, ipnet *net.IPNet) bool { return ipnet != nil && ipnet.Contains(ip.NetIP()) }
func ipv4Range(ipnet *net.IPNet) (IPv4, IPv4) {
	if ipnet == nil {
		return IPv4{}, IPv4{255, 255, 255, 255}
	}
	base := ipnet.IP.To4()
	mask := ipnet.Mask
	if base == nil || len(mask) != net.IPv4len {
		return IPv4{}, IPv4{255, 255, 255, 255}
	}
	var first, last IPv4
	for i := 0; i < net.IPv4len; i++ {
		first[i] = base[i] & mask[i]
		last[i] = first[i] | ^mask[i]
	}
	return first, last
}

type routingAudit struct {
	StartedAt       time.Time
	LastSummary     time.Time
	LastSpecial     time.Time
	ForcedVirtual   uint64
	BlockedOutbound uint64
	BlockedInbound  uint64
	RestoredInbound uint64
	OwnerUnknown    uint64
}

func newRoutingAudit() *routingAudit {
	now := time.Now()
	return &routingAudit{StartedAt: now, LastSummary: now}
}

func (a *routingAudit) NoteSpecial(action string, pid uint32, proto uint8, src IPv4, srcPort uint16, dst IPv4, dstPort uint16, rewritten IPv4) {
	if a == nil || (!a.LastSpecial.IsZero() && time.Since(a.LastSpecial) < 1200*time.Millisecond) {
		return
	}
	a.LastSpecial = time.Now()
	if rewritten != (IPv4{}) {
		log.Printf("strict %s pid=%d %s %s:%d -> %s:%d ==> %s", action, pid, protoName(proto), src, srcPort, dst, dstPort, rewritten)
		return
	}
	log.Printf("strict %s pid=%d %s %s:%d -> %s:%d", action, pid, protoName(proto), src, srcPort, dst, dstPort)
}

func (a *routingAudit) MaybeLogSummary() {
	if a == nil || time.Since(a.LastSummary) < 30*time.Second {
		return
	}
	a.LastSummary = time.Now()
	log.Printf("[STRICT-ROUTE] forced-virtual=%d restored-inbound=%d blocked-outbound=%d blocked-inbound=%d owner-unknown=%d uptime=%s",
		a.ForcedVirtual, a.RestoredInbound, a.BlockedOutbound, a.BlockedInbound,
		a.OwnerUnknown, time.Since(a.StartedAt).Round(time.Second))
}

func main() {
	// The standard logger writes routine Route Guard telemetry to stdout. The launcher
	// treats stderr as an error channel, so normal counters must not be misclassified.
	log.SetOutput(os.Stdout)

	var cfg Config
	flag.StringVar(&cfg.ClientIP, "client-ip", "", "assigned local virtual IP, e.g. 10.66.0.2")
	flag.StringVar(&cfg.AssignedIPFile, "assigned-ip-file", "", "optional file that contains assigned local virtual IP")
	flag.StringVar(&cfg.VirtualCIDR, "virtual-cidr", "10.66.0.0/24", "virtual LAN CIDR used by the SCBL tunnel")
	flag.StringVar(&cfg.Processes, "processes", "Blacklist_DX11_game.exe,Blacklist_game.exe", "comma-separated game process image names")
	flag.StringVar(&cfg.LogFile, "log", "", "optional log file")
	flag.StringVar(&cfg.WinDivertDir, "windivert-dir", "", "directory containing WinDivert.dll and WinDivert64.sys; defaults to current directory")
	flag.DurationVar(&cfg.NATTTL, "nat-ttl", defaultNATTTL, "NAT mapping TTL")
	flag.IntVar(&cfg.Priority, "priority", -1200, "WinDivert priority")
	flag.UintVar(&cfg.InterfaceIndex, "interface-index", 0, "EasyTier IPv4 interface index used for forced game routing")
	flag.StringVar(&cfg.SessionFile, "session-file", "", "launcher-owned JSON session/heartbeat file")
	flag.StringVar(&cfg.SessionID, "session-id", "", "launcher session id expected in the heartbeat file")
	flag.UintVar(&cfg.LauncherPID, "launcher-pid", 0, "launcher process id that owns this route-guard session")
	flag.DurationVar(&cfg.HeartbeatTimeout, "heartbeat-timeout", 2500*time.Millisecond, "maximum launcher heartbeat age before fail-open exit")
	flag.Parse()

	if cfg.LogFile != "" {
		if err := os.MkdirAll(filepath.Dir(cfg.LogFile), 0755); err != nil && filepath.Dir(cfg.LogFile) != "." {
			log.Printf("log directory warning: %v", err)
		}
		if f, err := os.OpenFile(cfg.LogFile, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644); err == nil {
			log.SetOutput(io.MultiWriter(os.Stdout, f))
			defer f.Close()
		}
	}

	if err := run(cfg); err != nil {
		log.Fatalf("scbl-process-router stopped: %v", err)
	}
}

func run(cfg Config) error {
	if strings.TrimSpace(cfg.ClientIP) == "" && cfg.AssignedIPFile != "" {
		data, err := os.ReadFile(cfg.AssignedIPFile)
		if err != nil {
			return fmt.Errorf("read assigned ip file failed: %w", err)
		}
		cfg.ClientIP = strings.TrimSpace(string(data))
	}
	clientIP, err := parseIPv4(cfg.ClientIP)
	if err != nil {
		return err
	}
	_, virtualNet, err := net.ParseCIDR(strings.TrimSpace(cfg.VirtualCIDR))
	if err != nil {
		return fmt.Errorf("invalid virtual CIDR %q: %w", cfg.VirtualCIDR, err)
	}

	if cfg.WinDivertDir != "" {
		if err := os.Chdir(cfg.WinDivertDir); err != nil {
			return fmt.Errorf("set WinDivert working directory failed: %w", err)
		}
	}
	if err := checkWinDivertFiles(); err != nil {
		return err
	}

	if strings.TrimSpace(cfg.SessionFile) == "" || strings.TrimSpace(cfg.SessionID) == "" || cfg.LauncherPID == 0 {
		return errors.New("launcher session arguments are required: -session-file, -session-id and -launcher-pid")
	}
	if cfg.HeartbeatTimeout < time.Second {
		cfg.HeartbeatTimeout = time.Second
	}
	session, err := newLauncherSessionGuard(cfg.SessionFile, cfg.SessionID, uint32(cfg.LauncherPID), cfg.HeartbeatTimeout)
	if err != nil {
		return fmt.Errorf("launcher session validation failed: %w", err)
	}

	log.Printf("SCBL process router starting")
	log.Printf("SCBL Route Guard v%s", routerVersion)
	log.Printf("launcher session=%s launcher-pid=%d heartbeat-timeout=%s initial-game-pids=%v", cfg.SessionID, cfg.LauncherPID, cfg.HeartbeatTimeout, session.TargetPIDList())
	log.Printf("client virtual ip=%s, virtual cidr=%s, interface-index=%d", clientIP.String(), virtualNet.String(), cfg.InterfaceIndex)
	log.Printf("strict routing mode enabled: only game PIDs authorized by the live launcher session are isolated")

	resolver := newOwnerResolver(session)
	resolver.Start()
	defer resolver.Stop()

	nat := newNATTable(cfg.NATTTL)
	defer nat.Stop()

	div, err := openDivert(int16(cfg.Priority))
	if err != nil {
		return err
	}
	defer div.Close()
	session.Start(func(reason string) {
		log.Printf("launcher session ended; route guard is switching to fail-open exit: %s", reason)
		div.Close()
	})
	defer session.Stop()

	_, virtualBroadcast := ipv4Range(virtualNet)
	audit := newRoutingAudit()
	log.Printf("WinDivert opened. Strict game isolation active; EasyTier handles native UDP broadcast relay, virtual broadcast=%s", virtualBroadcast.String())
	buf := make([]byte, divertBufSize)
	var addr divertAddress

	for {
		n, err := div.Recv(buf, &addr)
		if err != nil {
			if session.Expired() {
				log.Printf("route guard exited after launcher session loss: %s", session.ExpiredReason())
				return nil
			}
			return err
		}
		if n <= 0 {
			continue
		}
		pkt := buf[:n]
		meta, ok := parsePacket(pkt)
		if !ok {
			// Non-initial IPv4 fragments do not expose a TCP/UDP header at the network layer.
			// They are passed unchanged; normal SCBL MTU settings are intended to prevent them.
			_ = div.Send(pkt, &addr)
			continue
		}

		changed := false
		drop := false
		if addr.Outbound() {
			pid, known := resolver.ResolveOutbound(meta)
			if known && resolver.IsTargetPID(pid) {
				original := natMapping{
					Proto:      meta.Proto,
					LocalIP:    meta.SrcIP,
					LocalPort:  meta.SrcPort,
					RemoteIP:   meta.DstIP,
					RemotePort: meta.DstPort,
					UpdatedAt:  time.Now(),
				}

				switch {
				case ipInNet(meta.DstIP, virtualNet):
					// Pin only packets already addressed to the SCBL overlay. Existing
					// 10.66.0.255 broadcasts remain unchanged for EasyTier's native relay.
					isVirtualBroadcast := meta.Proto == protoUDP && meta.DstIP == virtualBroadcast
					if cfg.InterfaceIndex > 0 {
						addr.SetNetworkInterface(uint32(cfg.InterfaceIndex), 0)
					}
					if meta.SrcIP != clientIP {
						if isVirtualBroadcast {
							nat.PutWildcard(original)
						} else {
							nat.Put(original, meta.DstIP)
						}
						meta.SetSrcIP(clientIP)
						changed = true
					}
					audit.ForcedVirtual++

				default:
					// Strict mode intentionally prevents the original game process from bypassing
					// EasyTier through a physical adapter, Radmin, another VPN, or the public Internet.
					drop = true
					audit.BlockedOutbound++
					audit.NoteSpecial("blocked-out", pid, meta.Proto, meta.SrcIP, meta.SrcPort, meta.DstIP, meta.DstPort, IPv4{})
				}
			} else if resolver.HasTargetProcesses() && !known {
				audit.OwnerUnknown++
			}
		} else {
			pid, known := resolver.ResolveInbound(meta)
			if known && resolver.IsTargetPID(pid) {
				wrongInterface := cfg.InterfaceIndex > 0 && addr.NetworkInterface() != uint32(cfg.InterfaceIndex)
				if !ipInNet(meta.SrcIP, virtualNet) || wrongInterface {
					drop = true
					audit.BlockedInbound++
					audit.NoteSpecial("blocked-in", pid, meta.Proto, meta.SrcIP, meta.SrcPort, meta.DstIP, meta.DstPort, IPv4{})
				} else {
					// Restore the game's original local bind address when strict source NAT was
					// required. Wildcard mappings cover replies to converted broadcast/multicast.
					if original, ok := nat.Get(meta.Proto, meta.DstPort, meta.SrcPort, meta.SrcIP); ok {
						meta.SetSrcIP(original.RemoteIP)
						meta.SetDstIP(original.LocalIP)
						meta.SetSrcPort(original.RemotePort)
						meta.SetDstPort(original.LocalPort)
						changed = true
						audit.RestoredInbound++
					}
				}
			} else if resolver.HasTargetProcesses() && !known {
				audit.OwnerUnknown++
			}
		}

		audit.MaybeLogSummary()
		if drop {
			continue
		}
		if changed {
			if err := div.CalcChecksums(pkt, &addr); err != nil {
				log.Printf("checksum warning: %v", err)
			}
		}
		if err := div.Send(pkt, &addr); err != nil {
			log.Printf("WinDivertSend failed: %v", err)
		}
	}
}

func checkWinDivertFiles() error {
	if _, err := os.Stat("WinDivert.dll"); err != nil {
		return errors.New("missing WinDivert.dll; put WinDivert.dll in publish-single\\tools or run download_windivert.ps1")
	}
	if _, err := os.Stat("WinDivert64.sys"); err != nil {
		return errors.New("missing WinDivert64.sys; put WinDivert64.sys in publish-single\\tools or run download_windivert.ps1")
	}
	return nil
}

func normalizeProcessNames(s string) []string {
	parts := strings.Split(s, ",")
	seen := map[string]bool{}
	var out []string
	for _, p := range parts {
		p = strings.ToLower(strings.TrimSpace(p))
		if p == "" {
			continue
		}
		if !strings.HasSuffix(p, ".exe") {
			p += ".exe"
		}
		if !seen[p] {
			seen[p] = true
			out = append(out, p)
		}
	}
	return out
}

func protoName(p uint8) string {
	switch p {
	case protoTCP:
		return "TCP"
	case protoUDP:
		return "UDP"
	default:
		return fmt.Sprintf("IP%d", p)
	}
}

// ---------------- WinDivert dynamic wrapper ----------------

type divertAddress [96]byte

func (a *divertAddress) flags() uint64            { return binary.LittleEndian.Uint64(a[8:16]) }
func (a *divertAddress) Outbound() bool           { return a.flags()&(1<<17) != 0 }
func (a *divertAddress) NetworkInterface() uint32 { return binary.LittleEndian.Uint32(a[16:20]) }
func (a *divertAddress) SetNetworkInterface(ifIdx, subIfIdx uint32) {
	binary.LittleEndian.PutUint32(a[16:20], ifIdx)
	binary.LittleEndian.PutUint32(a[20:24], subIfIdx)
}

type Divert struct {
	handle       uintptr
	dll          *syscall.LazyDLL
	openProc     *syscall.LazyProc
	recvProc     *syscall.LazyProc
	sendProc     *syscall.LazyProc
	closeProc    *syscall.LazyProc
	checksumProc *syscall.LazyProc
}

func openDivert(priority int16) (*Divert, error) {
	d := &Divert{
		dll: syscall.NewLazyDLL("WinDivert.dll"),
	}
	d.openProc = d.dll.NewProc("WinDivertOpen")
	d.recvProc = d.dll.NewProc("WinDivertRecv")
	d.sendProc = d.dll.NewProc("WinDivertSend")
	d.checksumProc = d.dll.NewProc("WinDivertHelperCalcChecksums")
	d.closeProc = syscall.NewLazyDLL("kernel32.dll").NewProc("CloseHandle")

	// PID is not available in WinDivert's NETWORK layer, so strict isolation must inspect
	// every non-loopback IPv4 TCP/UDP packet and resolve socket ownership through IP Helper.
	// Packets not owned by the game are immediately reinjected unchanged.
	filter := "ip and (tcp or udp) and !loopback and !impostor"
	filterBytes := append([]byte(filter), 0)
	h, _, callErr := d.openProc.Call(
		uintptr(unsafe.Pointer(&filterBytes[0])),
		uintptr(windivertLayerNetwork),
		uintptr(priority),
		uintptr(0),
	)
	if h == ^uintptr(0) || h == 0 {
		if callErr != syscall.Errno(0) {
			return nil, fmt.Errorf("WinDivertOpen failed: %w", callErr)
		}
		return nil, errors.New("WinDivertOpen failed")
	}
	d.handle = h
	log.Printf("WinDivert filter: %s", filter)
	return d, nil
}

func (d *Divert) Recv(buf []byte, addr *divertAddress) (int, error) {
	var recvLen uint32
	r1, _, err := d.recvProc.Call(
		d.handle,
		uintptr(unsafe.Pointer(&buf[0])),
		uintptr(uint32(len(buf))),
		uintptr(unsafe.Pointer(&recvLen)),
		uintptr(unsafe.Pointer(addr)),
	)
	if r1 == 0 {
		if err != syscall.Errno(0) {
			return 0, err
		}
		return 0, errors.New("WinDivertRecv failed")
	}
	return int(recvLen), nil
}

func (d *Divert) Send(pkt []byte, addr *divertAddress) error {
	var sendLen uint32
	r1, _, err := d.sendProc.Call(
		d.handle,
		uintptr(unsafe.Pointer(&pkt[0])),
		uintptr(uint32(len(pkt))),
		uintptr(unsafe.Pointer(&sendLen)),
		uintptr(unsafe.Pointer(addr)),
	)
	if r1 == 0 {
		if err != syscall.Errno(0) {
			return err
		}
		return errors.New("WinDivertSend failed")
	}
	return nil
}

func (d *Divert) CalcChecksums(pkt []byte, addr *divertAddress) error {
	r1, _, err := d.checksumProc.Call(
		uintptr(unsafe.Pointer(&pkt[0])),
		uintptr(uint32(len(pkt))),
		uintptr(unsafe.Pointer(addr)),
		uintptr(0),
	)
	if r1 == 0 {
		if err != syscall.Errno(0) {
			return err
		}
		return errors.New("WinDivertHelperCalcChecksums failed")
	}
	return nil
}

func (d *Divert) Close() {
	if d.handle != 0 && d.handle != ^uintptr(0) {
		d.closeProc.Call(d.handle)
		d.handle = 0
	}
}

// ---------------- Packet parser / editor ----------------

type PacketMeta struct {
	Data       []byte
	IHL        int
	Proto      uint8
	SrcIP      IPv4
	DstIP      IPv4
	SrcPort    uint16
	DstPort    uint16
	PortOffset int
}

func parsePacket(pkt []byte) (*PacketMeta, bool) {
	if len(pkt) < 20 {
		return nil, false
	}
	if pkt[0]>>4 != 4 {
		return nil, false
	}
	ihl := int(pkt[0]&0x0F) * 4
	if ihl < 20 || len(pkt) < ihl+8 {
		return nil, false
	}
	proto := pkt[9]
	if proto != protoTCP && proto != protoUDP {
		return nil, false
	}
	src := ipv4FromPacket(pkt[12:16])
	dst := ipv4FromPacket(pkt[16:20])
	srcPort := binary.BigEndian.Uint16(pkt[ihl : ihl+2])
	dstPort := binary.BigEndian.Uint16(pkt[ihl+2 : ihl+4])
	return &PacketMeta{Data: pkt, IHL: ihl, Proto: proto, SrcIP: src, DstIP: dst, SrcPort: srcPort, DstPort: dstPort, PortOffset: ihl}, true
}

func (m *PacketMeta) SetSrcIP(ip IPv4) { copy(m.Data[12:16], ip[:]); m.SrcIP = ip }
func (m *PacketMeta) SetDstIP(ip IPv4) { copy(m.Data[16:20], ip[:]); m.DstIP = ip }
func (m *PacketMeta) SetSrcPort(port uint16) {
	binary.BigEndian.PutUint16(m.Data[m.PortOffset:m.PortOffset+2], port)
	m.SrcPort = port
}
func (m *PacketMeta) SetDstPort(port uint16) {
	binary.BigEndian.PutUint16(m.Data[m.PortOffset+2:m.PortOffset+4], port)
	m.DstPort = port
}

// ---------------- NAT state ----------------

type natKey struct {
	Proto      uint8
	LocalPort  uint16
	RemotePort uint16
	ReplySrcIP IPv4
}

type natMapping struct {
	Proto      uint8
	LocalIP    IPv4
	LocalPort  uint16
	RemoteIP   IPv4
	RemotePort uint16
	UpdatedAt  time.Time
}

type NATTable struct {
	mu       sync.RWMutex
	ttl      time.Duration
	mappings map[natKey]natMapping
	stop     chan struct{}
}

func newNATTable(ttl time.Duration) *NATTable {
	if ttl <= 0 {
		ttl = defaultNATTTL
	}
	n := &NATTable{ttl: ttl, mappings: map[natKey]natMapping{}, stop: make(chan struct{})}
	go n.cleanupLoop()
	return n
}

func (n *NATTable) Put(m natMapping, replySrcIP IPv4) {
	n.mu.Lock()
	n.mappings[natKey{Proto: m.Proto, LocalPort: m.LocalPort, RemotePort: m.RemotePort, ReplySrcIP: replySrcIP}] = m
	n.mu.Unlock()
}

func (n *NATTable) PutWildcard(m natMapping) {
	n.Put(m, IPv4{})
}

func (n *NATTable) Get(proto uint8, localPort, remotePort uint16, replySrcIP IPv4) (natMapping, bool) {
	n.mu.RLock()
	m, ok := n.mappings[natKey{Proto: proto, LocalPort: localPort, RemotePort: remotePort, ReplySrcIP: replySrcIP}]
	if !ok {
		m, ok = n.mappings[natKey{Proto: proto, LocalPort: localPort, RemotePort: remotePort, ReplySrcIP: IPv4{}}]
	}
	n.mu.RUnlock()
	return m, ok
}

func (n *NATTable) cleanupLoop() {
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-t.C:
			cutoff := time.Now().Add(-n.ttl)
			n.mu.Lock()
			for k, v := range n.mappings {
				if v.UpdatedAt.Before(cutoff) {
					delete(n.mappings, k)
				}
			}
			n.mu.Unlock()
		case <-n.stop:
			return
		}
	}
}

func (n *NATTable) Stop() { close(n.stop) }

// ---------------- launcher session / fail-open ownership ----------------

type launcherSessionState struct {
	SessionID          string   `json:"sessionId"`
	LauncherPID        uint32   `json:"launcherPid"`
	UpdatedAtUnixMs    int64    `json:"updatedAtUnixMs"`
	GamePIDs           []uint32 `json:"gamePids"`
	AllowEmptyGamePIDs bool     `json:"allowEmptyGamePids"`
}

type launcherSessionGuard struct {
	path             string
	expectedSession  string
	expectedLauncher uint32
	timeout          time.Duration
	mu               sync.RWMutex
	gamePIDs         map[uint32]bool
	expired          bool
	expiredReason    string
	stop             chan struct{}
	stopOnce         sync.Once
	expireOnce       sync.Once
	lastHeartbeatMs  int64
	lastTransientLog time.Time
}

func newLauncherSessionGuard(path, sessionID string, launcherPID uint32, timeout time.Duration) (*launcherSessionGuard, error) {
	g := &launcherSessionGuard{
		path:             filepath.Clean(path),
		expectedSession:  strings.TrimSpace(sessionID),
		expectedLauncher: launcherPID,
		timeout:          timeout,
		gamePIDs:         map[uint32]bool{},
		stop:             make(chan struct{}),
	}
	if err := g.refresh(); err != nil {
		return nil, err
	}
	return g, nil
}

func (g *launcherSessionGuard) Start(onExpired func(string)) {
	go func() {
		ticker := time.NewTicker(250 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				if err := g.refresh(); err != nil {
					g.expireOnce.Do(func() {
						g.mu.Lock()
						g.expired = true
						g.expiredReason = err.Error()
						g.gamePIDs = map[uint32]bool{}
						g.mu.Unlock()
						onExpired(err.Error())
					})
					return
				}
			case <-g.stop:
				return
			}
		}
	}()
}

func (g *launcherSessionGuard) Stop() {
	g.stopOnce.Do(func() { close(g.stop) })
}

func (g *launcherSessionGuard) refresh() error {
	var state launcherSessionState
	var transientErr error
	for attempt := 1; attempt <= 5; attempt++ {
		data, err := os.ReadFile(g.path)
		if err == nil {
			err = json.Unmarshal(data, &state)
		}
		if err == nil {
			transientErr = nil
			break
		}
		transientErr = err
		if attempt < 5 {
			time.Sleep(time.Duration(attempt*10) * time.Millisecond)
		}
	}

	if transientErr != nil {
		g.mu.RLock()
		lastHeartbeatMs := g.lastHeartbeatMs
		g.mu.RUnlock()
		if lastHeartbeatMs > 0 {
			age := time.Since(time.UnixMilli(lastHeartbeatMs))
			if age >= 0 && age <= g.timeout {
				if g.lastTransientLog.IsZero() || time.Since(g.lastTransientLog) >= 5*time.Second {
					g.lastTransientLog = time.Now()
					log.Printf("[SESSION-GUARD] transient heartbeat read/parse conflict retained last valid state: %v", transientErr)
				}
				return nil
			}
		}
		return fmt.Errorf("read launcher heartbeat failed after retries: %w", transientErr)
	}

	// Identity and process ownership failures are never treated as transient.
	if strings.TrimSpace(state.SessionID) != g.expectedSession {
		return fmt.Errorf("launcher session id changed")
	}
	if state.LauncherPID != g.expectedLauncher {
		return fmt.Errorf("launcher pid changed: expected=%d actual=%d", g.expectedLauncher, state.LauncherPID)
	}
	if !windowsProcessAlive(state.LauncherPID) {
		return fmt.Errorf("launcher process is no longer running: pid=%d", state.LauncherPID)
	}
	age := time.Since(time.UnixMilli(state.UpdatedAtUnixMs))
	if age < -5*time.Second || age > g.timeout {
		return fmt.Errorf("launcher heartbeat expired: age=%s timeout=%s", age.Round(time.Millisecond), g.timeout)
	}
	pids := map[uint32]bool{}
	for _, pid := range state.GamePIDs {
		if pid != 0 && windowsProcessAlive(pid) {
			pids[pid] = true
		}
	}
	if len(pids) == 0 && !state.AllowEmptyGamePIDs {
		return fmt.Errorf("launcher session has no live authorised game process")
	}
	g.mu.Lock()
	g.gamePIDs = pids
	g.lastHeartbeatMs = state.UpdatedAtUnixMs
	g.mu.Unlock()
	return nil
}

func (g *launcherSessionGuard) TargetPIDs() map[uint32]bool {
	g.mu.RLock()
	defer g.mu.RUnlock()
	out := make(map[uint32]bool, len(g.gamePIDs))
	for pid := range g.gamePIDs {
		out[pid] = true
	}
	return out
}

func (g *launcherSessionGuard) TargetPIDList() []uint32 {
	m := g.TargetPIDs()
	out := make([]uint32, 0, len(m))
	for pid := range m {
		out = append(out, pid)
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

func (g *launcherSessionGuard) Expired() bool {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return g.expired
}

func (g *launcherSessionGuard) ExpiredReason() string {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return g.expiredReason
}

func windowsProcessAlive(pid uint32) bool {
	if pid == 0 {
		return false
	}
	kernel32 := syscall.NewLazyDLL("kernel32.dll")
	openProcess := kernel32.NewProc("OpenProcess")
	getExitCodeProcess := kernel32.NewProc("GetExitCodeProcess")
	closeHandle := kernel32.NewProc("CloseHandle")
	const processQueryLimitedInformation = 0x1000
	handle, _, _ := openProcess.Call(processQueryLimitedInformation, 0, uintptr(pid))
	if handle == 0 {
		return false
	}
	defer closeHandle.Call(handle)
	var exitCode uint32
	ok, _, _ := getExitCodeProcess.Call(handle, uintptr(unsafe.Pointer(&exitCode)))
	return ok != 0 && exitCode == 259 // STILL_ACTIVE
}

// ---------------- Windows owner resolver ----------------

type ownerKey struct {
	Proto      uint8
	LocalIP    string
	LocalPort  uint16
	RemoteIP   string
	RemotePort uint16
}

type udpKey struct {
	LocalIP   string
	LocalPort uint16
}

type OwnerResolver struct {
	session         *launcherSessionGuard
	mu              sync.RWMutex
	refreshMu       sync.Mutex
	targetPIDs      map[uint32]bool
	tcpOwners       map[ownerKey]uint32
	tcpOwnersByPort map[uint16][]uint32
	udpOwners       map[udpKey][]uint32
	udpOwnersByPort map[uint16][]uint32
	refreshRequest  chan struct{}
	stop            chan struct{}
}

func newOwnerResolver(session *launcherSessionGuard) *OwnerResolver {
	return &OwnerResolver{
		session:         session,
		targetPIDs:      map[uint32]bool{},
		tcpOwners:       map[ownerKey]uint32{},
		tcpOwnersByPort: map[uint16][]uint32{},
		udpOwners:       map[udpKey][]uint32{},
		udpOwnersByPort: map[uint16][]uint32{},
		refreshRequest:  make(chan struct{}, 1),
		stop:            make(chan struct{}),
	}
}

func (r *OwnerResolver) Start() {
	r.refresh()
	go func() {
		t := time.NewTicker(500 * time.Millisecond)
		defer t.Stop()
		for {
			select {
			case <-t.C:
				r.refresh()
			case <-r.refreshRequest:
				r.refresh()
			case <-r.stop:
				return
			}
		}
	}()
}

func (r *OwnerResolver) Stop() { close(r.stop) }

func (r *OwnerResolver) IsTargetPID(pid uint32) bool {
	r.mu.RLock()
	ok := r.targetPIDs[pid]
	r.mu.RUnlock()
	return ok
}

func (r *OwnerResolver) HasTargetProcesses() bool {
	r.mu.RLock()
	has := len(r.targetPIDs) > 0
	r.mu.RUnlock()
	return has
}

func (r *OwnerResolver) ResolveOutbound(m *PacketMeta) (uint32, bool) {
	if pid, ok := r.Owner(m); ok {
		return pid, true
	}
	if r.HasTargetProcesses() {
		r.requestRefresh()
	}
	return 0, false
}

func (r *OwnerResolver) ResolveInbound(m *PacketMeta) (uint32, bool) {
	if pid, ok := r.OwnerInbound(m); ok {
		return pid, true
	}
	if r.HasTargetProcesses() {
		r.requestRefresh()
	}
	return 0, false
}

func (r *OwnerResolver) requestRefresh() {
	select {
	case r.refreshRequest <- struct{}{}:
	default:
	}
}

func (r *OwnerResolver) Owner(m *PacketMeta) (uint32, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if m.Proto == protoTCP {
		k := ownerKey{Proto: m.Proto, LocalIP: m.SrcIP.String(), LocalPort: m.SrcPort, RemoteIP: m.DstIP.String(), RemotePort: m.DstPort}
		if pid, ok := r.tcpOwners[k]; ok {
			return pid, true
		}
		// Some stacks expose 0.0.0.0 or do not populate the remote tuple immediately.
		// The fallback index is built during the background owner-table refresh so
		// the packet path remains O(1).
		if pids, ok := r.tcpOwnersByPort[m.SrcPort]; ok {
			return r.preferredPIDLocked(pids)
		}
	}
	if m.Proto == protoUDP {
		if pids, ok := r.udpOwners[udpKey{LocalIP: m.SrcIP.String(), LocalPort: m.SrcPort}]; ok {
			return r.preferredPIDLocked(pids)
		}
		if pids, ok := r.udpOwners[udpKey{LocalIP: "0.0.0.0", LocalPort: m.SrcPort}]; ok {
			return r.preferredPIDLocked(pids)
		}
		return r.ownerByUDPPortLocked(m.SrcPort)
	}
	return 0, false
}

func (r *OwnerResolver) OwnerInbound(m *PacketMeta) (uint32, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if m.Proto == protoTCP {
		k := ownerKey{Proto: m.Proto, LocalIP: m.DstIP.String(), LocalPort: m.DstPort, RemoteIP: m.SrcIP.String(), RemotePort: m.SrcPort}
		if pid, ok := r.tcpOwners[k]; ok {
			return pid, true
		}
		if pids, ok := r.tcpOwnersByPort[m.DstPort]; ok {
			return r.preferredPIDLocked(pids)
		}
	}
	if m.Proto == protoUDP {
		if pids, ok := r.udpOwners[udpKey{LocalIP: m.DstIP.String(), LocalPort: m.DstPort}]; ok {
			return r.preferredPIDLocked(pids)
		}
		if pids, ok := r.udpOwners[udpKey{LocalIP: "0.0.0.0", LocalPort: m.DstPort}]; ok {
			return r.preferredPIDLocked(pids)
		}
		return r.ownerByUDPPortLocked(m.DstPort)
	}
	return 0, false
}

func (r *OwnerResolver) preferredTargetPIDLocked(pids []uint32) (uint32, bool) {
	for _, pid := range pids {
		if r.targetPIDs[pid] {
			return pid, true
		}
	}
	return 0, false
}

func (r *OwnerResolver) preferredPIDLocked(pids []uint32) (uint32, bool) {
	if pid, ok := r.preferredTargetPIDLocked(pids); ok {
		return pid, true
	}
	if len(pids) > 0 && pids[0] != 0 {
		return pids[0], true
	}
	return 0, false
}

func appendUniquePID(items []uint32, pid uint32) []uint32 {
	if pid == 0 {
		return items
	}
	for _, existing := range items {
		if existing == pid {
			return items
		}
	}
	return append(items, pid)
}

func buildTCPPortIndex(owners map[ownerKey]uint32) map[uint16][]uint32 {
	index := make(map[uint16][]uint32)
	for key, pid := range owners {
		index[key.LocalPort] = appendUniquePID(index[key.LocalPort], pid)
	}
	return index
}

func buildUDPPortIndex(owners map[udpKey][]uint32) map[uint16][]uint32 {
	index := make(map[uint16][]uint32)
	for key, pids := range owners {
		for _, pid := range pids {
			index[key.LocalPort] = appendUniquePID(index[key.LocalPort], pid)
		}
	}
	return index
}

func (r *OwnerResolver) ownerByUDPPortLocked(port uint16) (uint32, bool) {
	pids, ok := r.udpOwnersByPort[port]
	if !ok {
		return 0, false
	}
	return r.preferredPIDLocked(pids)
}

func (r *OwnerResolver) refresh() {
	r.refreshMu.Lock()
	defer r.refreshMu.Unlock()
	r.refreshUnlocked()
}

func (r *OwnerResolver) refreshUnlocked() {
	pids := r.session.TargetPIDs()
	r.mu.Lock()
	r.targetPIDs = pids
	r.mu.Unlock()
	r.refreshOwnersUnlocked()
}

func (r *OwnerResolver) refreshOwnersUnlocked() {
	started := time.Now()
	tcpOwners, tcpErr := getTCPOwners()
	udpOwners, udpErr := getUDPOwners()
	if tcpErr != nil {
		log.Printf("tcp owner table warning: %v", tcpErr)
	}
	if udpErr != nil {
		log.Printf("udp owner table warning: %v", udpErr)
	}

	var tcpByPort map[uint16][]uint32
	var udpByPort map[uint16][]uint32
	if tcpErr == nil {
		tcpByPort = buildTCPPortIndex(tcpOwners)
	}
	if udpErr == nil {
		udpByPort = buildUDPPortIndex(udpOwners)
	}

	r.mu.Lock()
	if tcpErr == nil {
		r.tcpOwners = tcpOwners
		r.tcpOwnersByPort = tcpByPort
	}
	if udpErr == nil {
		r.udpOwners = udpOwners
		r.udpOwnersByPort = udpByPort
	}
	r.mu.Unlock()

	if elapsed := time.Since(started); elapsed >= 100*time.Millisecond {
		log.Printf("[OWNER-REFRESH] duration=%s tcp=%d udp=%d", elapsed.Round(time.Millisecond), len(tcpOwners), len(udpOwners))
	}
}

func findTargetPIDs(targetNames []string) map[uint32]bool {
	target := map[string]bool{}
	for _, n := range targetNames {
		target[strings.ToLower(n)] = true
	}
	out := map[uint32]bool{}

	cmd := exec.Command("tasklist", "/FO", "CSV", "/NH")
	raw, err := cmd.Output()
	if err != nil {
		return out
	}
	rd := csv.NewReader(bytes.NewReader(raw))
	rd.FieldsPerRecord = -1
	rows, err := rd.ReadAll()
	if err != nil {
		return out
	}
	for _, row := range rows {
		if len(row) < 2 {
			continue
		}
		image := strings.ToLower(strings.TrimSpace(row[0]))
		if !target[image] {
			continue
		}
		pid64, err := strconv.ParseUint(strings.TrimSpace(row[1]), 10, 32)
		if err == nil {
			out[uint32(pid64)] = true
		}
	}
	return out
}

func getTCPOwners() (map[ownerKey]uint32, error) {
	buf, err := callIPHelperTable("GetExtendedTcpTable", tcpTableOwnerPidAll)
	if err != nil {
		return nil, err
	}
	if len(buf) < 4 {
		return nil, errors.New("tcp table too small")
	}
	count := int(binary.LittleEndian.Uint32(buf[0:4]))
	owners := map[ownerKey]uint32{}
	const rowSize = 24
	for i := 0; i < count; i++ {
		off := 4 + i*rowSize
		if off+rowSize > len(buf) {
			break
		}
		row := buf[off : off+rowSize]
		localIP := IPv4{row[4], row[5], row[6], row[7]}.String()
		localPort := binary.BigEndian.Uint16(row[8:10])
		remoteIP := IPv4{row[12], row[13], row[14], row[15]}.String()
		remotePort := binary.BigEndian.Uint16(row[16:18])
		pid := binary.LittleEndian.Uint32(row[20:24])
		owners[ownerKey{Proto: protoTCP, LocalIP: localIP, LocalPort: localPort, RemoteIP: remoteIP, RemotePort: remotePort}] = pid
	}
	return owners, nil
}

func getUDPOwners() (map[udpKey][]uint32, error) {
	buf, err := callIPHelperTable("GetExtendedUdpTable", udpTableOwnerPid)
	if err != nil {
		return nil, err
	}
	if len(buf) < 4 {
		return nil, errors.New("udp table too small")
	}
	count := int(binary.LittleEndian.Uint32(buf[0:4]))
	owners := map[udpKey][]uint32{}
	const rowSize = 12
	for i := 0; i < count; i++ {
		off := 4 + i*rowSize
		if off+rowSize > len(buf) {
			break
		}
		row := buf[off : off+rowSize]
		localIP := IPv4{row[0], row[1], row[2], row[3]}.String()
		localPort := binary.BigEndian.Uint16(row[4:6])
		pid := binary.LittleEndian.Uint32(row[8:12])
		key := udpKey{LocalIP: localIP, LocalPort: localPort}
		duplicate := false
		for _, existing := range owners[key] {
			if existing == pid {
				duplicate = true
				break
			}
		}
		if !duplicate {
			owners[key] = append(owners[key], pid)
		}
	}
	return owners, nil
}

func callIPHelperTable(procName string, tableClass int) ([]byte, error) {
	dll := syscall.NewLazyDLL("iphlpapi.dll")
	proc := dll.NewProc(procName)
	var size uint32
	proc.Call(0, uintptr(unsafe.Pointer(&size)), uintptr(1), uintptr(afInet), uintptr(tableClass), uintptr(0))
	if size == 0 {
		return nil, fmt.Errorf("%s returned zero size", procName)
	}
	buf := make([]byte, size)
	r1, _, err := proc.Call(
		uintptr(unsafe.Pointer(&buf[0])),
		uintptr(unsafe.Pointer(&size)),
		uintptr(1),
		uintptr(afInet),
		uintptr(tableClass),
		uintptr(0),
	)
	if r1 != 0 {
		if err != syscall.Errno(0) {
			return nil, err
		}
		return nil, fmt.Errorf("%s failed, code=%d", procName, r1)
	}
	return buf[:size], nil
}
