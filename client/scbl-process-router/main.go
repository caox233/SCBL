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
	routerVersion         = "0.6.0"
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
	StatusFile       string
	HistoryFile      string
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

func isLimitedBroadcast(ip IPv4) bool {
	return ip == (IPv4{255, 255, 255, 255})
}

func isIPv4Multicast(ip IPv4) bool {
	return ip[0] >= 224 && ip[0] <= 239
}

func looksLikeDirectedBroadcast(ip IPv4) bool {
	// SCBL uses /24, and the legacy game/Radmin/typical home LANs observed in diagnostics
	// also use /24. Strict mode intentionally converts any x.x.x.255 game UDP destination.
	return ip[3] == 255
}

func shouldConvertToVirtualBroadcast(ip IPv4) bool {
	return isLimitedBroadcast(ip) || isIPv4Multicast(ip) || looksLikeDirectedBroadcast(ip)
}

type routingAudit struct {
	StartedAt              time.Time
	LastSummary            time.Time
	LastSpecial            time.Time
	LastFanout             time.Time
	ForcedVirtual          uint64
	VirtualBroadcast       uint64
	ConvertedBroadcast     uint64
	BroadcastFanoutPackets uint64
	BroadcastFanoutCopies  uint64
	BroadcastNoRecentPeer  uint64
	BroadcastFanoutFailed  uint64
	BlockedOutbound        uint64
	BlockedInbound         uint64
	RestoredInbound        uint64
	OwnerUnknown           uint64
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

func (a *routingAudit) NoteFanout(pid uint32, meta *PacketMeta, peers []IPv4, sent, failed int) {
	if a == nil || meta == nil || (!a.LastFanout.IsZero() && time.Since(a.LastFanout) < 1200*time.Millisecond) {
		return
	}
	a.LastFanout = time.Now()
	peerText := make([]string, 0, len(peers))
	for _, peer := range peers {
		peerText = append(peerText, peer.String())
	}
	log.Printf("[BROADCAST-FANOUT] pid=%d %s %s:%d -> %s:%d recent-peers=%v sent=%d failed=%d",
		pid, protoName(meta.Proto), meta.SrcIP, meta.SrcPort, meta.DstIP, meta.DstPort, peerText, sent, failed)
}

func (a *routingAudit) RecordFanout(pid uint32, meta *PacketMeta, peers []IPv4, sent, failed int) {
	if a == nil {
		return
	}
	if len(peers) == 0 {
		a.BroadcastNoRecentPeer++
		return
	}
	a.BroadcastFanoutPackets++
	a.BroadcastFanoutCopies += uint64(sent)
	a.BroadcastFanoutFailed += uint64(failed)
	a.NoteFanout(pid, meta, peers, sent, failed)
}

func sendBroadcastFanout(div *Divert, packet []byte, address divertAddress, clientIP IPv4, interfaceIndex uint, peers []IPv4) (sent, failed int) {
	if div == nil || len(packet) == 0 || len(peers) == 0 {
		return 0, 0
	}
	for _, peer := range peers {
		clone := append([]byte(nil), packet...)
		meta, ok := parsePacket(clone)
		if !ok || meta.Proto != protoUDP {
			failed++
			continue
		}
		meta.SetSrcIP(clientIP)
		meta.SetDstIP(peer)
		cloneAddress := address
		if interfaceIndex > 0 {
			cloneAddress.SetNetworkInterface(uint32(interfaceIndex), 0)
		}
		if err := div.CalcChecksums(clone, &cloneAddress); err != nil {
			log.Printf("broadcast fanout checksum warning peer=%s: %v", peer, err)
			failed++
			continue
		}
		if err := div.Send(clone, &cloneAddress); err != nil {
			log.Printf("broadcast fanout send failed peer=%s: %v", peer, err)
			failed++
			continue
		}
		sent++
	}
	return sent, failed
}

func (a *routingAudit) MaybeLogSummary() {
	if a == nil || time.Since(a.LastSummary) < 30*time.Second {
		return
	}
	a.LastSummary = time.Now()
	log.Printf("[STRICT-ROUTE] forced-virtual=%d virtual-broadcast=%d converted-broadcast=%d fanout-packets=%d fanout-copies=%d fanout-no-peer=%d fanout-failed=%d restored-inbound=%d blocked-outbound=%d blocked-inbound=%d owner-unknown=%d uptime=%s",
		a.ForcedVirtual, a.VirtualBroadcast, a.ConvertedBroadcast, a.BroadcastFanoutPackets, a.BroadcastFanoutCopies,
		a.BroadcastNoRecentPeer, a.BroadcastFanoutFailed, a.RestoredInbound, a.BlockedOutbound, a.BlockedInbound,
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
	flag.StringVar(&cfg.StatusFile, "status-file", "", "optional JSON status file for active game peer detection")
	flag.StringVar(&cfg.HistoryFile, "history-file", "", "optional JSONL history file for recent game traffic diagnostics")
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

	traffic := newGameTrafficTracker(clientIP, virtualNet, cfg.StatusFile, cfg.HistoryFile)
	defer traffic.Stop()

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
	log.Printf("WinDivert opened. Strict game isolation active; virtual broadcast=%s", virtualBroadcast.String())
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
					// Every SCBL unicast and subnet-broadcast packet is pinned to EasyTier,
					// even when Windows already selected the expected source address.
					isVirtualBroadcast := meta.Proto == protoUDP && meta.DstIP == virtualBroadcast
					if isVirtualBroadcast {
						audit.VirtualBroadcast++
					} else {
						traffic.Record(meta.DstIP, true, meta.Proto, len(pkt), meta.SrcPort, meta.DstPort, tcpSynWithoutAck(meta))
					}
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
					if isVirtualBroadcast {
						peers := traffic.RecentPeers(time.Now(), broadcastPeerRetention, maxBroadcastFanoutPeers)
						sent, failed := sendBroadcastFanout(div, pkt, addr, clientIP, cfg.InterfaceIndex, peers)
						audit.RecordFanout(pid, meta, peers, sent, failed)
					}
					audit.ForcedVirtual++

				case meta.Proto == protoUDP && shouldConvertToVirtualBroadcast(meta.DstIP):
					// Convert limited broadcasts, physical/Radmin directed broadcasts and
					// multicast into the SCBL subnet broadcast. Also duplicate the packet as
					// unicast to recently active game peers, preserving migration discovery
					// when native subnet broadcast is unavailable but unicast is healthy.
					if cfg.InterfaceIndex > 0 {
						addr.SetNetworkInterface(uint32(cfg.InterfaceIndex), 0)
					}
					nat.PutWildcard(original)
					oldDst := meta.DstIP
					meta.SetSrcIP(clientIP)
					meta.SetDstIP(virtualBroadcast)
					changed = true
					audit.ConvertedBroadcast++
					audit.NoteSpecial("converted", pid, meta.Proto, original.LocalIP, original.LocalPort, oldDst, original.RemotePort, virtualBroadcast)
					peers := traffic.RecentPeers(time.Now(), broadcastPeerRetention, maxBroadcastFanoutPeers)
					sent, failed := sendBroadcastFanout(div, pkt, addr, clientIP, cfg.InterfaceIndex, peers)
					audit.RecordFanout(pid, meta, peers, sent, failed)

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
					traffic.Record(meta.SrcIP, false, meta.Proto, len(pkt), meta.DstPort, meta.SrcPort, tcpSynWithoutAck(meta))
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

func tcpSynWithoutAck(m *PacketMeta) bool {
	if m == nil || m.Proto != protoTCP || len(m.Data) <= m.PortOffset+13 {
		return false
	}
	flags := m.Data[m.PortOffset+13]
	return flags&0x02 != 0 && flags&0x10 == 0
}

// ---------------- Game traffic status ----------------

const (
	gameTrafficPort          = 13000
	gameTrafficWindow        = 3 * time.Second
	gamePeerRecentThreshold  = 1500 * time.Millisecond
	gameCandidateConfirmRuns = 3
	gameInitialConfirmRuns   = 2
	broadcastPeerRetention   = 10 * time.Minute
	maxBroadcastFanoutPeers  = 16
)

type trafficSample struct {
	At       time.Time
	Outbound bool
	Bytes    uint64
	Proto    uint8
}

type peerTraffic struct {
	IP        IPv4
	LastSeen  time.Time
	Samples   []trafficSample
	Protocols map[string]bool
}

type gameTrafficTracker struct {
	mu                     sync.Mutex
	localIP                IPv4
	virtualNet             *net.IPNet
	statusFile             string
	historyFile            string
	lastHistoryWrite       time.Time
	lastHistoryFingerprint string
	peers                  map[IPv4]*peerTraffic
	recentPeers            map[IPv4]time.Time
	networkAddress         IPv4
	broadcastAddress       IPv4
	stop                   chan struct{}
	currentRole            string
	currentPeer            IPv4
	pendingRole            string
	pendingPeer            IPv4
	pendingCount           int
}

type gamePeerStatus struct {
	IP                   string   `json:"ip"`
	OutboundPackets      uint64   `json:"outboundPackets"`
	InboundPackets       uint64   `json:"inboundPackets"`
	OutboundBytes        uint64   `json:"outboundBytes"`
	InboundBytes         uint64   `json:"inboundBytes"`
	LastSeenUnixMs       int64    `json:"lastSeenUnixMs"`
	Protocols            []string `json:"protocols"`
	OutboundAverageBytes uint64   `json:"outboundAverageBytes"`
	InboundAverageBytes  uint64   `json:"inboundAverageBytes"`
	OutboundMaxBytes     uint64   `json:"outboundMaxBytes"`
	InboundMaxBytes      uint64   `json:"inboundMaxBytes"`
}

type gameRouteStatus struct {
	UpdatedAtUnixMs int64            `json:"updatedAtUnixMs"`
	LocalIP         string           `json:"localIp"`
	Role            string           `json:"role"`
	PrimaryPeerIP   string           `json:"primaryPeerIp"`
	CandidatePeerIP string           `json:"candidatePeerIp,omitempty"`
	Confidence      int              `json:"confidence"`
	ActivePeerCount int              `json:"activePeerCount"`
	WindowMs        int64            `json:"windowMs"`
	DetectionMode   string           `json:"detectionMode"`
	Peers           []gamePeerStatus `json:"peers"`
}

type peerWindowStats struct {
	IP                   IPv4
	LastSeen             time.Time
	OutboundPackets      uint64
	InboundPackets       uint64
	OutboundBytes        uint64
	InboundBytes         uint64
	Protocols            []string
	OutboundAverageBytes uint64
	InboundAverageBytes  uint64
	OutboundMaxBytes     uint64
	InboundMaxBytes      uint64
	Score                uint64
}

type rawGameHostCandidate struct {
	Role       string
	Peer       IPv4
	Confidence int
}

func newGameTrafficTracker(localIP IPv4, virtualNet *net.IPNet, statusFile, historyFile string) *gameTrafficTracker {
	networkAddress, broadcastAddress := ipv4Range(virtualNet)
	t := &gameTrafficTracker{
		localIP:          localIP,
		virtualNet:       virtualNet,
		statusFile:       strings.TrimSpace(statusFile),
		historyFile:      strings.TrimSpace(historyFile),
		peers:            map[IPv4]*peerTraffic{},
		recentPeers:      map[IPv4]time.Time{},
		networkAddress:   networkAddress,
		broadcastAddress: broadcastAddress,
		stop:             make(chan struct{}),
		currentRole:      "unknown",
	}
	if t.statusFile != "" {
		_ = os.MkdirAll(filepath.Dir(t.statusFile), 0755)
		_ = os.Remove(t.statusFile)
		if t.historyFile != "" {
			_ = os.MkdirAll(filepath.Dir(t.historyFile), 0755)
			_ = os.Remove(t.historyFile)
		}
		go t.writeLoop()
	}
	return t
}

// Record observes only traffic that Windows attributes to the game process. Host detection is
// intentionally narrower than routing: only the game's UDP/13000 peer traffic participates in
// host inference. Login, service, EasyTier, probe, and unrelated virtual-LAN packets are ignored.
func (t *gameTrafficTracker) Record(remote IPv4, outbound bool, proto uint8, size int, localPort, remotePort uint16, syn bool) {
	if t == nil || remote == t.localIP || remote == t.networkAddress || remote == t.broadcastAddress || remote.String() == "10.66.0.1" || !ipInNet(remote, t.virtualNet) {
		return
	}
	if proto != protoUDP || (localPort != gameTrafficPort && remotePort != gameTrafficPort) {
		return
	}

	now := time.Now()
	t.mu.Lock()
	t.recentPeers[remote] = now
	if t.statusFile != "" {
		p := t.peers[remote]
		if p == nil {
			p = &peerTraffic{IP: remote, Protocols: map[string]bool{}}
			t.peers[remote] = p
		}
		p.LastSeen = now
		p.Protocols[protoName(proto)] = true
		p.Samples = append(p.Samples, trafficSample{At: now, Outbound: outbound, Bytes: uint64(size), Proto: proto})
	}
	_ = syn
	t.mu.Unlock()
}

func (t *gameTrafficTracker) RecentPeers(now time.Time, maxAge time.Duration, limit int) []IPv4 {
	if t == nil || maxAge <= 0 || limit <= 0 {
		return nil
	}
	type recentPeer struct {
		IP       IPv4
		LastSeen time.Time
	}
	cutoff := now.Add(-maxAge)
	t.mu.Lock()
	items := make([]recentPeer, 0, len(t.recentPeers))
	for ip, lastSeen := range t.recentPeers {
		if lastSeen.Before(cutoff) {
			delete(t.recentPeers, ip)
			continue
		}
		if ip == t.localIP || ip == t.networkAddress || ip == t.broadcastAddress || ip.String() == "10.66.0.1" || !ipInNet(ip, t.virtualNet) {
			continue
		}
		items = append(items, recentPeer{IP: ip, LastSeen: lastSeen})
	}
	t.mu.Unlock()
	sort.Slice(items, func(i, j int) bool {
		if items[i].LastSeen.Equal(items[j].LastSeen) {
			return items[i].IP.String() < items[j].IP.String()
		}
		return items[i].LastSeen.After(items[j].LastSeen)
	})
	if len(items) > limit {
		items = items[:limit]
	}
	out := make([]IPv4, 0, len(items))
	for _, item := range items {
		out = append(out, item.IP)
	}
	return out
}

func (t *gameTrafficTracker) writeLoop() {
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			t.writeSnapshot()
		case <-t.stop:
			t.writeSnapshot()
			return
		}
	}
}

func (t *gameTrafficTracker) writeSnapshot() {
	if t.statusFile == "" {
		return
	}
	now := time.Now()
	t.mu.Lock()
	stats := t.collectWindowStatsLocked(now)
	raw := inferGameHostCandidate(stats, now)
	status := t.stabilizeCandidateLocked(stats, raw, now)
	t.mu.Unlock()

	rawJSON, err := json.MarshalIndent(status, "", "  ")
	if err != nil {
		return
	}
	tmp := t.statusFile + ".tmp"
	if err := os.WriteFile(tmp, rawJSON, 0644); err != nil {
		return
	}
	_ = os.Remove(t.statusFile)
	_ = os.Rename(tmp, t.statusFile)

	if t.historyFile != "" {
		fingerprint := fmt.Sprintf("%s|%s|%s|%d", status.Role, status.PrimaryPeerIP, status.CandidatePeerIP, status.ActivePeerCount)
		changed := fingerprint != t.lastHistoryFingerprint
		due := t.lastHistoryWrite.IsZero() || now.Sub(t.lastHistoryWrite) >= 10*time.Second
		if changed || due {
			t.lastHistoryWrite = now
			t.lastHistoryFingerprint = fingerprint
			compact, err := json.Marshal(status)
			if err == nil {
				if info, statErr := os.Stat(t.historyFile); statErr == nil && info.Size() > 1024*1024 {
					_ = os.Remove(t.historyFile + ".1")
					_ = os.Rename(t.historyFile, t.historyFile+".1")
				}
				if f, openErr := os.OpenFile(t.historyFile, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644); openErr == nil {
					_, _ = f.Write(append(compact, '\n'))
					_ = f.Close()
				}
			}
		}
	}
}

func (t *gameTrafficTracker) collectWindowStatsLocked(now time.Time) []peerWindowStats {
	cutoff := now.Add(-gameTrafficWindow)
	stats := make([]peerWindowStats, 0, len(t.peers))
	for ip, p := range t.peers {
		first := 0
		for first < len(p.Samples) && p.Samples[first].At.Before(cutoff) {
			first++
		}
		if first > 0 {
			p.Samples = append([]trafficSample(nil), p.Samples[first:]...)
		}
		if len(p.Samples) == 0 {
			if now.Sub(p.LastSeen) > gameTrafficWindow {
				delete(t.peers, ip)
			}
			continue
		}

		item := peerWindowStats{IP: p.IP, LastSeen: p.LastSeen}
		protocols := map[string]bool{}
		for _, sample := range p.Samples {
			protocols[protoName(sample.Proto)] = true
			if sample.Outbound {
				item.OutboundPackets++
				item.OutboundBytes += sample.Bytes
				if sample.Bytes > item.OutboundMaxBytes {
					item.OutboundMaxBytes = sample.Bytes
				}
			} else {
				item.InboundPackets++
				item.InboundBytes += sample.Bytes
				if sample.Bytes > item.InboundMaxBytes {
					item.InboundMaxBytes = sample.Bytes
				}
			}
		}
		for proto := range protocols {
			item.Protocols = append(item.Protocols, proto)
		}
		if item.OutboundPackets > 0 {
			item.OutboundAverageBytes = item.OutboundBytes / item.OutboundPackets
		}
		if item.InboundPackets > 0 {
			item.InboundAverageBytes = item.InboundBytes / item.InboundPackets
		}
		if len(item.Protocols) == 2 && item.Protocols[0] > item.Protocols[1] {
			item.Protocols[0], item.Protocols[1] = item.Protocols[1], item.Protocols[0]
		}
		// Packet cadence is a stronger host signal than payload size. Bytes have a low weight so
		// one large state packet cannot outweigh sustained two-way communication.
		biDirectionalBonus := uint64(0)
		if item.OutboundPackets > 0 && item.InboundPackets > 0 {
			biDirectionalBonus = 200_000
		}
		item.Score = biDirectionalBonus + (item.OutboundPackets+item.InboundPackets)*4096 + (item.OutboundBytes+item.InboundBytes)/16
		stats = append(stats, item)
	}
	sort.Slice(stats, func(i, j int) bool {
		if stats[i].Score == stats[j].Score {
			return stats[i].IP.String() < stats[j].IP.String()
		}
		return stats[i].Score > stats[j].Score
	})
	return stats
}

func inferGameHostCandidate(stats []peerWindowStats, now time.Time) rawGameHostCandidate {
	eligible := make([]peerWindowStats, 0, len(stats))
	for _, item := range stats {
		if now.Sub(item.LastSeen) <= gamePeerRecentThreshold && item.OutboundPackets >= 2 && item.InboundPackets >= 2 {
			eligible = append(eligible, item)
		}
	}
	if len(eligible) == 0 {
		return rawGameHostCandidate{Role: "unknown"}
	}
	if len(eligible) == 1 {
		return rawGameHostCandidate{Role: "client", Peer: eligible[0].IP, Confidence: 96}
	}

	// The July three-player trace proved that SCBL exchanges UDP/13000 packets in a full mesh.
	// Therefore "two peers means I am host" is invalid. Never infer a local host from topology.
	// A remote host is reported only when one flow strongly and consistently dominates the next
	// strongest flow. Balanced multi-peer traffic remains unknown instead of displaying a false 0ms.
	top := eligible[0]
	second := eligible[1]
	strongScoreLead := top.Score >= second.Score*5/2
	strongPacketLead := (top.OutboundPackets + top.InboundPackets) >= (second.OutboundPackets+second.InboundPackets)*2
	if strongScoreLead && strongPacketLead {
		return rawGameHostCandidate{Role: "client", Peer: top.IP, Confidence: 84}
	}
	return rawGameHostCandidate{Role: "unknown", Peer: top.IP, Confidence: 0}
}

func (t *gameTrafficTracker) stabilizeCandidateLocked(stats []peerWindowStats, raw rawGameHostCandidate, now time.Time) gameRouteStatus {
	status := gameRouteStatus{
		UpdatedAtUnixMs: now.UnixMilli(),
		LocalIP:         t.localIP.String(),
		Role:            "unknown",
		ActivePeerCount: len(stats),
		WindowMs:        gameTrafficWindow.Milliseconds(),
		DetectionMode:   "game-process-udp-13000-fallback-v4-strict-route",
		Peers:           make([]gamePeerStatus, 0, len(stats)),
	}
	for _, item := range stats {
		status.Peers = append(status.Peers, gamePeerStatus{
			IP:                   item.IP.String(),
			OutboundPackets:      item.OutboundPackets,
			InboundPackets:       item.InboundPackets,
			OutboundBytes:        item.OutboundBytes,
			InboundBytes:         item.InboundBytes,
			LastSeenUnixMs:       item.LastSeen.UnixMilli(),
			Protocols:            item.Protocols,
			OutboundAverageBytes: item.OutboundAverageBytes,
			InboundAverageBytes:  item.InboundAverageBytes,
			OutboundMaxBytes:     item.OutboundMaxBytes,
			InboundMaxBytes:      item.InboundMaxBytes,
		})
	}
	if raw.Peer != (IPv4{}) {
		status.CandidatePeerIP = raw.Peer.String()
	}

	currentStillRecent := false
	if t.currentRole == "client" {
		for _, item := range stats {
			if item.IP == t.currentPeer && now.Sub(item.LastSeen) <= gamePeerRecentThreshold {
				currentStillRecent = true
				break
			}
		}
	}

	sameAsCurrent := raw.Role == t.currentRole && (raw.Role != "client" || raw.Peer == t.currentPeer)
	if sameAsCurrent {
		t.pendingRole = ""
		t.pendingPeer = IPv4{}
		t.pendingCount = 0
	} else if raw.Role == "unknown" {
		if t.currentRole != "unknown" {
			log.Printf("[HOST-DETECT] cleared previous-role=%s previous-target=%s reason=balanced-or-ambiguous-full-mesh active-peers=%d", t.currentRole, t.currentPeer.String(), len(stats))
		}
		t.currentRole = "unknown"
		t.currentPeer = IPv4{}
		t.pendingRole = ""
		t.pendingPeer = IPv4{}
		t.pendingCount = 0
	} else {
		samePending := raw.Role == t.pendingRole && (raw.Role != "client" || raw.Peer == t.pendingPeer)
		if samePending {
			t.pendingCount++
		} else {
			t.pendingRole = raw.Role
			t.pendingPeer = raw.Peer
			t.pendingCount = 1
		}

		required := gameCandidateConfirmRuns
		if t.currentRole == "unknown" || !currentStillRecent {
			required = gameInitialConfirmRuns
		}
		if t.pendingCount >= required {
			previousRole := t.currentRole
			previousPeer := t.currentPeer
			t.currentRole = raw.Role
			t.currentPeer = raw.Peer
			t.pendingRole = ""
			t.pendingPeer = IPv4{}
			t.pendingCount = 0
			previousTarget := previousPeer.String()
			currentTarget := t.currentPeer.String()
			log.Printf("[HOST-DETECT] confirmed role=%s target=%s previous-role=%s previous-target=%s confidence=%d active-peers=%d window=%s",
				t.currentRole, currentTarget, previousRole, previousTarget, raw.Confidence, len(stats), gameTrafficWindow)
		}
	}

	status.Role = t.currentRole
	if t.currentRole == "client" {
		status.PrimaryPeerIP = t.currentPeer.String()
		status.Confidence = raw.Confidence
	}
	return status
}

func (t *gameTrafficTracker) Stop() {
	if t == nil {
		return
	}
	select {
	case <-t.stop:
	default:
		close(t.stop)
	}
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
	lastFastRefresh time.Time
	targetPIDs      map[uint32]bool
	tcpOwners       map[ownerKey]uint32
	udpOwners       map[udpKey][]uint32
	stop            chan struct{}
}

func newOwnerResolver(session *launcherSessionGuard) *OwnerResolver {
	return &OwnerResolver{session: session, stop: make(chan struct{})}
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
		r.fastRefresh()
		return r.Owner(m)
	}
	return 0, false
}

func (r *OwnerResolver) ResolveInbound(m *PacketMeta) (uint32, bool) {
	if pid, ok := r.OwnerInbound(m); ok {
		return pid, true
	}
	if r.HasTargetProcesses() {
		r.fastRefresh()
		return r.OwnerInbound(m)
	}
	return 0, false
}

func (r *OwnerResolver) fastRefresh() {
	r.refreshMu.Lock()
	defer r.refreshMu.Unlock()
	if !r.lastFastRefresh.IsZero() && time.Since(r.lastFastRefresh) < 120*time.Millisecond {
		return
	}
	r.lastFastRefresh = time.Now()
	r.refreshOwnersUnlocked()
}

func (r *OwnerResolver) Owner(m *PacketMeta) (uint32, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if m.Proto == protoTCP {
		k := ownerKey{Proto: m.Proto, LocalIP: m.SrcIP.String(), LocalPort: m.SrcPort, RemoteIP: m.DstIP.String(), RemotePort: m.DstPort}
		if pid, ok := r.tcpOwners[k]; ok {
			return pid, true
		}
		// Some stacks expose 0.0.0.0 or do not populate remote tuple immediately; fall back to local endpoint.
		for key, pid := range r.tcpOwners {
			if key.LocalPort == m.SrcPort && key.Proto == m.Proto {
				return pid, true
			}
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
		for key, pid := range r.tcpOwners {
			if key.LocalPort == m.DstPort && key.Proto == m.Proto {
				return pid, true
			}
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

func (r *OwnerResolver) ownerByUDPPortLocked(port uint16) (uint32, bool) {
	var fallback uint32
	for key, pids := range r.udpOwners {
		if key.LocalPort != port {
			continue
		}
		if pid, ok := r.preferredTargetPIDLocked(pids); ok {
			return pid, true
		}
		if fallback == 0 && len(pids) > 0 {
			fallback = pids[0]
		}
	}
	return fallback, fallback != 0
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
	tcpOwners, tcpErr := getTCPOwners()
	udpOwners, udpErr := getUDPOwners()
	if tcpErr != nil {
		log.Printf("tcp owner table warning: %v", tcpErr)
	}
	if udpErr != nil {
		log.Printf("udp owner table warning: %v", udpErr)
	}
	r.mu.Lock()
	if tcpErr == nil {
		r.tcpOwners = tcpOwners
	}
	if udpErr == nil {
		r.udpOwners = udpOwners
	}
	r.mu.Unlock()
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
