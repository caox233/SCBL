use std::collections::HashSet;
use std::ffi::c_char;
use std::ffi::c_int;
use std::ffi::c_void;
use std::ffi::CStr;
use std::net::Ipv4Addr;
use std::sync::atomic::AtomicBool;
use std::sync::atomic::Ordering;
use std::sync::Mutex;
use std::sync::OnceLock;

use dll_syringe::function::FunctionPtr;
use retour::static_detour;
use tracing::info;
use tracing::instrument;
use tracing::warn;
use windows::core::s;
use windows::Win32::Foundation::FreeLibrary;
use windows::Win32::Networking::WinSock::WSAGetLastError;
use windows::Win32::Networking::WinSock::WSASetLastError;
use windows::Win32::Networking::WinSock::AF_INET;
use windows::Win32::Networking::WinSock::SOCKADDR;
use windows::Win32::Networking::WinSock::WSA_ERROR;
use windows::Win32::System::LibraryLoader::GetProcAddress;
use windows::Win32::System::LibraryLoader::LoadLibraryA;

use crate::addresses::Addresses;
use crate::config;
use crate::config::Config;
use crate::config::Hook;

static_detour! {
    static SomeEventHook: unsafe extern "thiscall" fn(*mut c_void, *mut c_void, *mut c_void) -> *mut c_void;
    static SomeEvent2Hook: unsafe extern "thiscall" fn(*mut c_void, *mut c_void, *mut c_void, *mut c_void, *mut c_void, *mut c_void) -> *mut c_void;
    static BindSocketHook: unsafe extern "stdcall" fn(usize, *const SOCKADDR, c_int) -> c_int;
    static ConnectHook: unsafe extern "stdcall" fn(usize, *const SOCKADDR, c_int) -> c_int;
    static WsaConnectHook: unsafe extern "stdcall" fn(usize, *const SOCKADDR, c_int, *mut c_void, *mut c_void, *mut c_void, *mut c_void) -> c_int;
    static CloseSocketHook: unsafe extern "stdcall" fn(usize) -> c_int;
    static SendToHook: unsafe extern "stdcall" fn(usize, *const c_char, c_int, c_int, *const SOCKADDR, c_int) -> c_int;
    static RecvFromHook: unsafe extern "stdcall" fn(usize, *const c_char, c_int, c_int, *const SOCKADDR, *mut c_int) -> c_int;
    static EventMaybeQueuePopHook: unsafe extern "thiscall" fn(usize) -> *const  *const *const c_void;
    static EventHandlerHook: unsafe extern "thiscall" fn(*mut c_void,*mut c_void,*mut c_void,*mut c_void,*mut c_void,*mut c_void) -> usize;
}

static LOG_STORM_PACKETS: AtomicBool = AtomicBool::new(false);

const WSAEWOULDBLOCK_CODE: c_int = 10035;
const WSAEINPROGRESS_CODE: c_int = 10036;
const WSAEALREADY_CODE: c_int = 10037;

fn to_hex_stream(data: &[u8]) -> String {
    data.iter().fold(String::new(), |mut output, b| {
        use std::fmt::Write;
        let _ = write!(output, "{b:02x}");
        output
    })
}

fn deref_addr<'a, T>(addr: *const T) -> Option<&'a T> {
    if !addr.is_aligned() {
        return None;
    }
    unsafe { addr.as_ref() }
}

#[repr(C)]
#[derive(Clone, Copy)]
struct SockAddrInRaw {
    sin_family: u16,
    sin_port: [u8; 2],
    sin_addr: [u8; 4],
    sin_zero: [u8; 8],
}

static BOUND_SOCKETS: OnceLock<Mutex<HashSet<usize>>> = OnceLock::new();

fn bound_sockets() -> &'static Mutex<HashSet<usize>> {
    BOUND_SOCKETS.get_or_init(|| Mutex::new(HashSet::new()))
}

fn get_bind_ip() -> Option<Ipv4Addr> {
    config::get()?.networking.ip_address
}

fn sockaddr_ipv4(addr: *const SOCKADDR, len: c_int) -> Option<Ipv4Addr> {
    if addr.is_null() || len < 8 {
        return None;
    }
    let addr = unsafe { addr.as_ref()? };
    if addr.sa_family != AF_INET {
        return None;
    }
    Some(Ipv4Addr::new(addr.sa_data[2] as u8, addr.sa_data[3] as u8, addr.sa_data[4] as u8, addr.sa_data[5] as u8))
}

fn sockaddr_port(addr: *const SOCKADDR, len: c_int) -> Option<u16> {
    if addr.is_null() || len < 4 {
        return None;
    }
    let addr = unsafe { addr.as_ref()? };
    if addr.sa_family != AF_INET {
        return None;
    }
    Some(u16::from_be_bytes([addr.sa_data[0] as u8, addr.sa_data[1] as u8]))
}

fn sockaddr_port_bytes(addr: *const SOCKADDR, len: c_int) -> [u8; 2] {
    if addr.is_null() || len < 4 {
        return [0, 0];
    }
    unsafe { addr.as_ref() }.map(|addr| [addr.sa_data[0] as u8, addr.sa_data[1] as u8]).unwrap_or([0, 0])
}

fn is_loopback(ip: Ipv4Addr) -> bool {
    ip.octets()[0] == 127
}

fn is_socket_marked_bound(socket: usize) -> bool {
    bound_sockets().lock().map_or(false, |sockets| sockets.contains(&socket))
}

fn mark_socket_bound(socket: usize) {
    if let Ok(mut sockets) = bound_sockets().lock() {
        sockets.insert(socket);
    }
}

fn unmark_socket_bound(socket: usize) {
    if let Ok(mut sockets) = bound_sockets().lock() {
        sockets.remove(&socket);
    }
}

fn make_bind_addr(bind_ip: Ipv4Addr, port: [u8; 2]) -> SockAddrInRaw {
    SockAddrInRaw {
        sin_family: 2,
        sin_port: port,
        sin_addr: bind_ip.octets(),
        sin_zero: [0; 8],
    }
}

fn should_rewrite_explicit_bind(original_ip: Ipv4Addr, bind_ip: Ipv4Addr) -> bool {
    original_ip != bind_ip && !is_loopback(original_ip)
}

fn packet_logging_enabled() -> bool {
    LOG_STORM_PACKETS.load(Ordering::Relaxed)
}

fn is_connect_pending_error(error: WSA_ERROR) -> bool {
    matches!(error.0, WSAEWOULDBLOCK_CODE | WSAEINPROGRESS_CODE | WSAEALREADY_CODE)
}

fn restore_wsa_last_error(error: WSA_ERROR) {
    unsafe { WSASetLastError(error.0) };
}

fn force_bind_socket(socket: usize, reason: &str, target: *const SOCKADDR, target_len: c_int) {
    let Some(bind_ip) = get_bind_ip() else {
        return;
    };
    if is_socket_marked_bound(socket) {
        return;
    }
    let Some(target_ip) = sockaddr_ipv4(target, target_len) else {
        return;
    };
    let target_port = sockaddr_port(target, target_len).unwrap_or(0);
    let bind_addr = make_bind_addr(bind_ip, [0, 0]);
    let result = unsafe {
        BindSocketHook.call(
            socket,
            (&bind_addr as *const SockAddrInRaw).cast::<SOCKADDR>(),
            std::mem::size_of::<SockAddrInRaw>() as c_int,
        )
    };
    if result == 0 {
        mark_socket_bound(socket);
        info!("PublicVirtualNet socket bind [{reason}]: socket={socket}, local={bind_ip}:0, target={target_ip}:{target_port}");
    } else {
        let error = unsafe { WSAGetLastError() };
        warn!("PublicVirtualNet socket bind failed [{reason}]: socket={socket}, local={bind_ip}:0, target={target_ip}:{target_port}, wsa_error={error:?}");
        restore_wsa_last_error(error);
    }
}

// Winsock detours deliberately do not use #[instrument]. Dropping a tracing span after
// WSASetLastError can call other Windows APIs and overwrite the caller-visible error.
fn bind_socket(socket: usize, name: *const SOCKADDR, namelen: c_int) -> c_int {
    let requested_ip = sockaddr_ipv4(name, namelen);
    let requested_port = sockaddr_port(name, namelen).unwrap_or(0);
    let Some(bind_ip) = get_bind_ip() else {
        let result = unsafe { BindSocketHook.call(socket, name, namelen) };
        if result == 0 {
            mark_socket_bound(socket);
        }
        return result;
    };
    let Some(original_ip) = requested_ip else {
        let result = unsafe { BindSocketHook.call(socket, name, namelen) };
        if result == 0 {
            mark_socket_bound(socket);
        }
        return result;
    };
    let result = if should_rewrite_explicit_bind(original_ip, bind_ip) {
        let port = sockaddr_port_bytes(name, namelen);
        let bind_addr = make_bind_addr(bind_ip, port);
        info!("PublicVirtualNet explicit bind rewrite: socket={socket}, requested={original_ip}:{requested_port}, effective={bind_ip}:{requested_port}");
        unsafe {
            BindSocketHook.call(
                socket,
                (&bind_addr as *const SockAddrInRaw).cast::<SOCKADDR>(),
                std::mem::size_of::<SockAddrInRaw>() as c_int,
            )
        }
    } else {
        unsafe { BindSocketHook.call(socket, name, namelen) }
    };
    if result == 0 {
        mark_socket_bound(socket);
    } else {
        let error = unsafe { WSAGetLastError() };
        warn!("PublicVirtualNet explicit bind failed: socket={socket}, requested={original_ip}:{requested_port}, configured={bind_ip}, wsa_error={error:?}");
        restore_wsa_last_error(error);
    }
    result
}

fn connect_socket(socket: usize, name: *const SOCKADDR, namelen: c_int) -> c_int {
    let target_ip = sockaddr_ipv4(name, namelen);
    let target_port = sockaddr_port(name, namelen);
    force_bind_socket(socket, "connect", name, namelen);
    let result = unsafe { ConnectHook.call(socket, name, namelen) };
    if result != 0 {
        let error = unsafe { WSAGetLastError() };
        if is_connect_pending_error(error) {
            info!("PublicVirtualNet connect pending: socket={socket}, target={target_ip:?}:{target_port:?}, wsa_error={error:?}");
        } else {
            warn!("PublicVirtualNet connect failed: socket={socket}, target={target_ip:?}:{target_port:?}, wsa_error={error:?}");
        }
        restore_wsa_last_error(error);
    } else if packet_logging_enabled() {
        info!("PublicVirtualNet connect succeeded: socket={socket}, target={target_ip:?}:{target_port:?}");
    }
    result
}

fn wsa_connect_socket(socket: usize, name: *const SOCKADDR, namelen: c_int, caller_data: *mut c_void, callee_data: *mut c_void, sqos: *mut c_void, gqos: *mut c_void) -> c_int {
    let target_ip = sockaddr_ipv4(name, namelen);
    let target_port = sockaddr_port(name, namelen);
    force_bind_socket(socket, "WSAConnect", name, namelen);
    let result = unsafe { WsaConnectHook.call(socket, name, namelen, caller_data, callee_data, sqos, gqos) };
    if result != 0 {
        let error = unsafe { WSAGetLastError() };
        if is_connect_pending_error(error) {
            info!("PublicVirtualNet WSAConnect pending: socket={socket}, target={target_ip:?}:{target_port:?}, wsa_error={error:?}");
        } else {
            warn!("PublicVirtualNet WSAConnect failed: socket={socket}, target={target_ip:?}:{target_port:?}, wsa_error={error:?}");
        }
        restore_wsa_last_error(error);
    } else if packet_logging_enabled() {
        info!("PublicVirtualNet WSAConnect succeeded: socket={socket}, target={target_ip:?}:{target_port:?}");
    }
    result
}

fn close_socket(socket: usize) -> c_int {
    let result = unsafe { CloseSocketHook.call(socket) };
    if result == 0 {
        unmark_socket_bound(socket);
    } else {
        let error = unsafe { WSAGetLastError() };
        warn!("PublicVirtualNet closesocket failed: socket={socket}, wsa_error={error:?}");
        restore_wsa_last_error(error);
    }
    result
}

fn get_storm_event_name<'a>(instance_addr: *const c_void) -> Option<&'a CStr> {
    let vtable_addr = *deref_addr(instance_addr.cast::<*const *const usize>())?;
    let event_type_method_addr = *deref_addr(unsafe { vtable_addr.add(1) })?;
    if event_type_method_addr.is_null() {
        return None;
    }
    let op = unsafe { *event_type_method_addr.cast::<u16>() };
    // 83 D3 cmp r/m32, imm8
    if op != 0x3d83 {
        return None;
    }
    let imm = unsafe { *event_type_method_addr.cast::<u8>().add(2 + std::mem::size_of::<*const u8>()) };
    if imm != 0 {
        return None;
    }
    let mut id_addr = *deref_addr(unsafe { std::ptr::read_unaligned(event_type_method_addr.cast::<u16>().add(1).cast::<*const *const c_char>()) })?;
    if id_addr.is_null() {
        let func_ptr: extern "thiscall" fn(*const c_void) -> *const *const c_char = unsafe { std::mem::transmute(event_type_method_addr) };
        id_addr = *deref_addr((func_ptr)(instance_addr))?;
    }
    unsafe { Some(CStr::from_ptr(id_addr)) }
}

unsafe fn event_probe_read_u32(base: *const c_void, offset: usize) -> Option<u32> {
    if base.is_null() {
        None
    } else {
        Some(std::ptr::read_unaligned(base.cast::<u8>().add(offset).cast::<u32>()))
    }
}

unsafe fn event_probe_read_ptr(base: *const c_void, offset: usize) -> *const c_void {
    if base.is_null() {
        std::ptr::null()
    } else {
        std::ptr::read_unaligned(base.cast::<u8>().add(offset).cast::<*const c_void>())
    }
}

fn storm_event_probe(marker: &str, owner: *const c_void, event: *const c_void) {
    unsafe {
        let owner_vtable = event_probe_read_ptr(owner, 0x00);
        let owner_vslot_70 = event_probe_read_ptr(owner_vtable, 0x70);
        let event_vtable = event_probe_read_ptr(event, 0x00);
        let event_code = event_probe_read_u32(event, 0x08);
        let event_session_id = event_probe_read_u32(event, 0x0c);
        let event_name = get_storm_event_name(event)
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| String::from("?"));
        info!(
            "{marker} owner={owner:?} owner_vtable={owner_vtable:?} owner_vslot_70={owner_vslot_70:?} routes_join_completion={} event={event:?} event_vtable={event_vtable:?} event_name={event_name:?} event_word_4={:?} event_code={event_code:?} event_remainder={:?} event_session_id={event_session_id:?} event_word_10={:?} event_word_14={:?}",
            owner_vslot_70 as usize == 0x007bdfb0,
            event_probe_read_u32(event, 0x04),
            event_code.map(|value| value % 1000),
            event_probe_read_u32(event, 0x10),
            event_probe_read_u32(event, 0x14),
        );
    }
}

#[instrument]
fn some_event_hook(this: *mut c_void, a: *mut c_void, b: *mut c_void) -> *mut c_void {
    storm_event_probe("AsmStormDispatch1 phase=enter", this as *const c_void, b as *const c_void);
    let result = unsafe { SomeEventHook.call(this, a, b) };
    info!("AsmStormDispatch1 phase=leave this={this:?} arg_a={a:?} event={b:?} result={result:?}");
    result
}

#[instrument(skip(this, arg1, arg2, arg3))]
fn some_event2(this: *mut c_void, arg1: *mut c_void, arg2: *mut c_void, arg3: *mut c_void, arg4: *mut c_void, arg5: *mut c_void) -> *mut c_void {
    storm_event_probe("AsmStormDispatch2 phase=enter", this as *const c_void, arg3 as *const c_void);
    info!("AsmStormDispatch2 args this={this:?} arg1={arg1:?} arg2={arg2:?} event={arg3:?} arg4={arg4:?} arg5={arg5:?}");
    let result = unsafe { SomeEvent2Hook.call(this, arg1, arg2, arg3, arg4, arg5) };
    info!("AsmStormDispatch2 phase=leave this={this:?} event={arg3:?} result={result:?}");
    result
}

fn sendto(s: usize, buf: *const c_char, len: c_int, flag: c_int, to: *const SOCKADDR, tolen: c_int) -> c_int {
    force_bind_socket(s, "sendto", to, tolen);
    let target_ip = sockaddr_ipv4(to, tolen);
    let target_port = sockaddr_port(to, tolen);
    if packet_logging_enabled() && target_port == Some(13000) {
        info!("PublicVirtualNet sendto: socket={s}, target={target_ip:?}:{target_port:?}, bytes={len}");
        if !buf.is_null() && len > 0 {
            #[allow(clippy::cast_sign_loss)]
            let data = unsafe { std::slice::from_raw_parts(buf.cast::<u8>(), len as usize) };
            info!("sendto: {}", to_hex_stream(data));
        }
    }
    let result = unsafe { SendToHook.call(s, buf, len, flag, to, tolen) };
    if result < 0 {
        let error = unsafe { WSAGetLastError() };
        warn!("PublicVirtualNet sendto failed: socket={s}, target={target_ip:?}:{target_port:?}, bytes={len}, wsa_error={error:?}");
        restore_wsa_last_error(error);
    }
    result
}

fn recvfrom(s: usize, buf: *const c_char, len: c_int, flag: c_int, from: *const SOCKADDR, fromlen: *mut c_int) -> c_int {
    let outlen = unsafe { RecvFromHook.call(s, buf, len, flag, from, fromlen) };
    if outlen < 0 {
        let error = unsafe { WSAGetLastError() };
        warn!("PublicVirtualNet recvfrom failed: socket={s}, requested_bytes={len}, wsa_error={error:?}");
        restore_wsa_last_error(error);
        return outlen;
    }
    let source_len = if fromlen.is_null() { 0 } else { unsafe { *fromlen } };
    let source_ip = sockaddr_ipv4(from, source_len);
    let source_port = sockaddr_port(from, source_len);
    if packet_logging_enabled() && source_port == Some(13000) && outlen > 0 {
        info!("PublicVirtualNet recvfrom: socket={s}, source={source_ip:?}:{source_port:?}, bytes={outlen}");
        if !buf.is_null() {
            #[allow(clippy::cast_sign_loss)]
            let data = unsafe { std::slice::from_raw_parts(buf.cast::<u8>(), outlen as usize) };
            info!("recvfrom: {}", to_hex_stream(data));
        }
    }
    outlen
}

#[instrument]
fn event_queue_pop(this: usize) -> *const *const *const c_void {
    let res = unsafe { EventMaybeQueuePopHook.call(this) };
    let node = if res.is_null() { std::ptr::null() } else { unsafe { *res.add(1) } };
    let event = if node.is_null() { std::ptr::null() } else { unsafe { *node } };
    info!("AsmStormQueuePop queue=0x{this:x} result={res:?} node={node:?} event={event:?}");
    if !event.is_null() {
        storm_event_probe("AsmStormQueuePop event", this as *const c_void, event);
    }
    res
}

#[instrument]
fn event_handler(this: *mut c_void, param_1: *mut c_void, param_2: *mut c_void, param_3: *mut c_void, param_4: *mut c_void, param_5: *mut c_void) -> usize {
    storm_event_probe("AsmStormEventHandler phase=enter", this as *const c_void, param_3 as *const c_void);
    info!("AsmStormEventHandler args this={this:?} param1={param_1:?} param2={param_2:?} event={param_3:?} param4={param_4:?} param5={param_5:?}");
    let result = unsafe { EventHandlerHook.call(this, param_1, param_2, param_3, param_4, param_5) };
    info!("AsmStormEventHandler phase=leave this={this:?} event={param_3:?} result=0x{result:x}");
    result
}

pub unsafe fn init_hooks(config: &Config, addr: &Addresses) {
    LOG_STORM_PACKETS.store(config.enable_all_hooks || config.enable_hooks.contains(&Hook::StormPackets), Ordering::Relaxed);
    super::configurable_hook!(config, Hook::StormEventDispatcher, SomeEventHook; addr.func_storm_event_dispatch => some_event_hook);
    super::configurable_hook!(config, Hook::StormEventDispatcher, SomeEvent2Hook; addr.func_storm_event_dispatch2 => some_event2);
    super::configurable_hook!(config, Hook::StormEventDispatcher, EventMaybeQueuePopHook; addr.func_storm_event_maybe_queue_pop => event_queue_pop);
    super::configurable_hook!(config, Hook::StormEventDispatcher, EventHandlerHook; addr.func_storm_event_handler => event_handler);
    if let Ok(lib) = LoadLibraryA(s!("ws2_32.dll")) {
        let bind_addr = GetProcAddress(lib, s!("bind"));
        let connect_addr = GetProcAddress(lib, s!("connect"));
        let wsa_connect_addr = GetProcAddress(lib, s!("WSAConnect"));
        let close_socket_addr = GetProcAddress(lib, s!("closesocket"));
        let sendto_addr = GetProcAddress(lib, s!("sendto"));
        let recvfrom_addr = GetProcAddress(lib, s!("recvfrom"));

        if config.networking.ip_address.is_some() {
            info!("PublicVirtualNet enabling mandatory Winsock binding hooks for configured BindIP");
            super::hook!(BindSocketHook, bind_addr, bind_socket);
            super::hook!(ConnectHook, connect_addr, connect_socket);
            super::hook!(WsaConnectHook, wsa_connect_addr, wsa_connect_socket);
            super::hook!(CloseSocketHook, close_socket_addr, close_socket);
            super::hook!(SendToHook, sendto_addr, sendto);
            super::hook!(RecvFromHook, recvfrom_addr, recvfrom);
        } else {
            if let Some(sendto_addr) = sendto_addr {
                super::configurable_hook!(config, Hook::StormPackets, SendToHook; Some(sendto_addr.as_ptr()) => sendto);
            }
            if let Some(recvfrom_addr) = recvfrom_addr {
                super::configurable_hook!(config, Hook::StormPackets, RecvFromHook; Some(recvfrom_addr.as_ptr()) => recvfrom);
            }
        }
        let _ = FreeLibrary(lib);
    }
}

pub unsafe fn deinit_hooks(config: &Config) {
    super::disable_configurable_hook!(config, Hook::StormEventDispatcher, SomeEventHook);
    super::disable_configurable_hook!(config, Hook::StormEventDispatcher, SomeEvent2Hook);
    super::disable_configurable_hook!(config, Hook::StormEventDispatcher, EventHandlerHook);
    super::disable_configurable_hook!(config, Hook::StormEventDispatcher, EventMaybeQueuePopHook);
    let _ = BindSocketHook.disable();
    let _ = ConnectHook.disable();
    let _ = WsaConnectHook.disable();
    let _ = CloseSocketHook.disable();
    let _ = SendToHook.disable();
    let _ = RecvFromHook.disable();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_storm_event_name() {
        let name = b"hello world\0";
        let name_addr = name.as_ptr();
        let id_obj = [name_addr];
        let id_addr: [u8; std::mem::size_of::<*const u8>()] = unsafe { std::mem::transmute(id_obj.as_ptr()) };
        let mut func = vec![0x83, 0x3d];
        for c in id_addr {
            func.push(c);
        }
        func.push(0);
        let vtable = [std::ptr::null(), func.as_ptr()];
        let instance = [vtable.as_ptr()];
        let evt_name = get_storm_event_name(instance.as_ptr().cast());
        assert!(evt_name.is_some());
        assert!(evt_name.unwrap().to_str().unwrap() == "hello world");
    }

    #[test]
    fn explicit_bind_rewrite_preserves_loopback_and_virtual_ip() {
        let virtual_ip = Ipv4Addr::new(10, 66, 0, 3);
        assert!(should_rewrite_explicit_bind(Ipv4Addr::UNSPECIFIED, virtual_ip));
        assert!(should_rewrite_explicit_bind(Ipv4Addr::new(192, 168, 1, 20), virtual_ip));
        assert!(!should_rewrite_explicit_bind(virtual_ip, virtual_ip));
        assert!(!should_rewrite_explicit_bind(Ipv4Addr::LOCALHOST, virtual_ip));
    }

    #[test]
    fn nonblocking_connect_errors_are_pending_not_fatal() {
        assert!(is_connect_pending_error(WSA_ERROR(WSAEWOULDBLOCK_CODE)));
        assert!(is_connect_pending_error(WSA_ERROR(WSAEINPROGRESS_CODE)));
        assert!(is_connect_pending_error(WSA_ERROR(WSAEALREADY_CODE)));
        assert!(!is_connect_pending_error(WSA_ERROR(10054)));
        assert!(!is_connect_pending_error(WSA_ERROR(10061)));
    }
}
