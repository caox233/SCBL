use std::collections::HashSet;
use std::ffi::c_char;
use std::ffi::c_int;
use std::ffi::c_void;
use std::ffi::CStr;
use std::net::Ipv4Addr;
use std::sync::{Mutex, OnceLock};

use dll_syringe::function::FunctionPtr;
use retour::static_detour;
use tracing::info;
use tracing::instrument;
use tracing::warn;
use windows::core::s;
use windows::Win32::Foundation::FreeLibrary;
use windows::Win32::Networking::WinSock::AF_INET;
use windows::Win32::Networking::WinSock::SOCKADDR;
use windows::Win32::Networking::WinSock::WSAGetLastError;
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

    Some(Ipv4Addr::new(
        addr.sa_data[2] as u8,
        addr.sa_data[3] as u8,
        addr.sa_data[4] as u8,
        addr.sa_data[5] as u8,
    ))
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

    let addr = unsafe { addr.as_ref() };
    if let Some(addr) = addr {
        return [addr.sa_data[0] as u8, addr.sa_data[1] as u8];
    }

    [0, 0]
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

fn force_bind_socket(socket: usize, reason: &str, target: *const SOCKADDR, target_len: c_int) {
    let Some(bind_ip) = get_bind_ip() else {
        return;
    };

    if is_socket_marked_bound(socket) {
        return;
    }

    // 只处理 IPv4 目标。公网专版目标通常是 10.66.0.1。
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
        info!(
            "PublicVirtualNet socket bind [{reason}]: socket={socket}, {bind_ip}:0 -> {target_ip}:{target_port}"
        );
    } else {
        // 如果 socket 已经被游戏提前绑定，二次 bind 会失败。这里标记为已处理，避免每个包刷日志。
        let err = unsafe { WSAGetLastError() };
        mark_socket_bound(socket);
        warn!(
            "PublicVirtualNet socket bind failed [{reason}]: socket={socket}, {bind_ip}:0 -> {target_ip}:{target_port}, wsa_error={err:?}."
        );
    }
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
        // info!("no cmp");
        return None;
    }
    let imm = unsafe { *event_type_method_addr.cast::<u8>().add(2 + std::mem::size_of::<*const u8>()) };
    if imm != 0 {
        // info!("non zero");
        return None;
    }
    let mut id_addr = *deref_addr(unsafe { std::ptr::read_unaligned(event_type_method_addr.cast::<u16>().add(1).cast::<*const *const c_char>()) })?;
    if id_addr.is_null() {
        // info!("id_addr is null");
        // return None;
        let func_ptr: extern "thiscall" fn(*const c_void) -> *const *const c_char = unsafe { std::mem::transmute(event_type_method_addr) };
        id_addr = *deref_addr((func_ptr)(instance_addr))?;
    }
    unsafe { Some(CStr::from_ptr(id_addr)) }
}

#[instrument]
fn some_event_hook(this: *mut c_void, a: *mut c_void, b: *mut c_void) -> *mut c_void {
    if let Some(evt_name) = get_storm_event_name(b.cast()) {
        info!("event: {evt_name:?}");
    }
    unsafe { SomeEventHook.call(this, a, b) }
}

#[instrument(skip(this, arg1, arg2, arg3))]
fn some_event2(this: *mut c_void, arg1: *mut c_void, arg2: *mut c_void, arg3: *mut c_void, arg4: *mut c_void, arg5: *mut c_void) -> *mut c_void {
    if let Some(evt_name) = get_storm_event_name(arg3.cast()) {
        info!("event2: {evt_name:?}");
    }
    unsafe { SomeEvent2Hook.call(this, arg1, arg2, arg3, arg4, arg5) }
}

#[instrument(skip_all)]
fn bind_socket(socket: usize, name: *const SOCKADDR, namelen: c_int) -> c_int {
    let Some(bind_ip) = get_bind_ip() else {
        let result = unsafe { BindSocketHook.call(socket, name, namelen) };
        if result == 0 {
            mark_socket_bound(socket);
        }
        return result;
    };

    let Some(original_ip) = sockaddr_ipv4(name, namelen) else {
        let result = unsafe { BindSocketHook.call(socket, name, namelen) };
        if result == 0 {
            mark_socket_bound(socket);
        }
        return result;
    };

    if should_rewrite_explicit_bind(original_ip, bind_ip) {
        let port = sockaddr_port_bytes(name, namelen);
        let port_number = u16::from_be_bytes(port);
        let bind_addr = make_bind_addr(bind_ip, port);
        info!(
            "PublicVirtualNet socket bind rewrite: socket={socket}, {original_ip}:{port_number} -> {bind_ip}:{port_number}"
        );
        let result = unsafe {
            BindSocketHook.call(
                socket,
                (&bind_addr as *const SockAddrInRaw).cast::<SOCKADDR>(),
                std::mem::size_of::<SockAddrInRaw>() as c_int,
            )
        };
        if result == 0 {
            mark_socket_bound(socket);
        }
        return result;
    }

    let result = unsafe { BindSocketHook.call(socket, name, namelen) };
    if result == 0 {
        mark_socket_bound(socket);
    }
    result
}

#[instrument(skip_all)]
fn connect_socket(socket: usize, name: *const SOCKADDR, namelen: c_int) -> c_int {
    force_bind_socket(socket, "connect", name, namelen);
    unsafe { ConnectHook.call(socket, name, namelen) }
}

#[instrument(skip_all)]
fn wsa_connect_socket(
    socket: usize,
    name: *const SOCKADDR,
    namelen: c_int,
    caller_data: *mut c_void,
    callee_data: *mut c_void,
    sqos: *mut c_void,
    gqos: *mut c_void,
) -> c_int {
    force_bind_socket(socket, "WSAConnect", name, namelen);
    unsafe { WsaConnectHook.call(socket, name, namelen, caller_data, callee_data, sqos, gqos) }
}

#[instrument(skip_all)]
fn close_socket(socket: usize) -> c_int {
    unmark_socket_bound(socket);
    unsafe { CloseSocketHook.call(socket) }
}

#[instrument(skip_all)]
fn sendto(s: usize, buf: *const c_char, len: c_int, flag: c_int, to: *const SOCKADDR, tolen: c_int) -> c_int {
    force_bind_socket(s, "sendto", to, tolen);

    if let Some(to_ref) = unsafe { to.as_ref() } {
        let port = 13000u16;
        #[allow(clippy::cast_possible_truncation)]
        if !buf.is_null() && to_ref.sa_family == AF_INET && to_ref.sa_data[0] == (port >> 8) as i8 && to_ref.sa_data[1] == port as i8 {
            #[allow(clippy::cast_sign_loss)]
            let data = unsafe { std::slice::from_raw_parts(buf.cast::<u8>(), len as usize) };
            info!("sendto: {}", to_hex_stream(data));
        }
    }
    unsafe { SendToHook.call(s, buf, len, flag, to, tolen) }
}

#[instrument(skip_all)]
fn recvfrom(s: usize, buf: *const c_char, len: c_int, flag: c_int, from: *const SOCKADDR, fromlen: *mut c_int) -> c_int {
    let outlen = unsafe { RecvFromHook.call(s, buf, len, flag, from, fromlen) };
    if let Some(from_ref) = unsafe { from.as_ref() } {
        let port = 13000u16;
        #[allow(clippy::cast_possible_truncation)]
        if !buf.is_null() && outlen > 0 && from_ref.sa_family == AF_INET && from_ref.sa_data[0] == (port >> 8) as i8 && from_ref.sa_data[1] == port as i8 {
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
    if !res.is_null() {
        let instance_addr = unsafe { **res.add(1) };
        if let Some(evt_name) = get_storm_event_name(instance_addr) {
            info!("event: {evt_name:?}");
        }
    }
    res
}

#[instrument]
fn event_handler(this: *mut c_void, param_1: *mut c_void, param_2: *mut c_void, param_3: *mut c_void, param_4: *mut c_void, param_5: *mut c_void) -> usize {
    if !param_3.is_null() {
        if let Some(evt_name) = get_storm_event_name(param_3.cast()) {
            info!("event: {evt_name:?}");
        }
    }
    unsafe { EventHandlerHook.call(this, param_1, param_2, param_3, param_4, param_5) }
}

pub unsafe fn init_hooks(config: &Config, addr: &Addresses) {
    super::configurable_hook!(config, Hook::StormEventDispatcher, SomeEventHook; addr.func_storm_event_dispatch => some_event_hook);
    super::configurable_hook!(config, Hook::StormEventDispatcher, SomeEvent2Hook; addr.func_storm_event_dispatch2 => some_event2);
    super::configurable_hook!(config, Hook::StormEventDispatcher, EventMaybeQueuePopHook; addr.func_storm_event_maybe_queue_pop => event_queue_pop);
    super::configurable_hook!(config, Hook::StormEventDispatcher, EventHandlerHook; addr.func_storm_event_handler => event_handler);
    if let Ok(lib) = LoadLibraryA(s!("ws2_32.dll")) {
        // 公网专版：Winsock 绑定 Hook 不依赖 StormPackets 开关。
        // 只要 5th_auth.dat 里存在 BindIP，就会把游戏 IPv4 socket 强制绑定到该虚拟网源地址。
        if let Some(bind_addr) = GetProcAddress(lib, s!("bind")) {
            super::hook!(BindSocketHook, Some(bind_addr.as_ptr()), bind_socket);
        }
        if let Some(connect_addr) = GetProcAddress(lib, s!("connect")) {
            super::hook!(ConnectHook, Some(connect_addr.as_ptr()), connect_socket);
        }
        if let Some(wsa_connect_addr) = GetProcAddress(lib, s!("WSAConnect")) {
            super::hook!(WsaConnectHook, Some(wsa_connect_addr.as_ptr()), wsa_connect_socket);
        }
        if let Some(close_socket_addr) = GetProcAddress(lib, s!("closesocket")) {
            super::hook!(CloseSocketHook, Some(close_socket_addr.as_ptr()), close_socket);
        }
        if let Some(sendto_addr) = GetProcAddress(lib, s!("sendto")) {
            super::hook!(SendToHook, Some(sendto_addr.as_ptr()), sendto);
        }
        if let Some(recvfrom_addr) = GetProcAddress(lib, s!("recvfrom")) {
            super::hook!(RecvFromHook, Some(recvfrom_addr.as_ptr()), recvfrom);
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
}
