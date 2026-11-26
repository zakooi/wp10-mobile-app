mod http_client;
mod layout_engine;

use http_client::{execute_http_request, WsHttpResponse};
use layout_engine::{render_html_to_display_list, WsRenderBuffer};
use std::env;
use std::slice;
use std::str;

const VERSION_MAJOR: u32 = 0;
const VERSION_MINOR: u32 = 3;
const VERSION_PATCH: u32 = 0;

/// Copies `text` as UTF-8 bytes into `out_buf` (truncating cleanly at a UTF-8 char boundary).
/// Returns the number of bytes written, or -1 if `out_buf` is null or `out_cap` is 0.
fn write_utf8_to_buf(text: &str, out_buf: *mut u8, out_cap: usize) -> i32 {
    if out_buf.is_null() || out_cap == 0 {
        return -1;
    }

    let bytes = text.as_bytes();
    let mut len = bytes.len().min(out_cap);
    while len > 0 && !text.is_char_boundary(len) {
        len -= 1;
    }

    // SAFETY: Caller guarantees `out_buf` points to writable memory of at least `out_cap` bytes.
    let dst = unsafe { slice::from_raw_parts_mut(out_buf, out_cap) };
    dst[..len].copy_from_slice(&bytes[..len]);
    len as i32
}

/// SAFETY: Caller guarantees `ptr` is valid for `len` bytes when non-null and `len > 0`.
unsafe fn read_input_bytes<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if ptr.is_null() || len == 0 {
        &[]
    } else {
        slice::from_raw_parts(ptr, len)
    }
}

/// Computes a deterministic 32-bit FNV-1a checksum to verify non-trivial execution across FFI.
fn fnv1a_32(data: &[u8]) -> u32 {
    let mut hash: u32 = 0x811c_9dc5;
    for &b in data {
        hash ^= b as u32;
        hash = hash.wrapping_mul(0x0100_0193);
    }
    hash
}

/// Returns the packed engine version: `(major << 16) | (minor << 8) | patch`.
#[no_mangle]
pub extern "C" fn ws_core_version() -> u32 {
    (VERSION_MAJOR << 16) | (VERSION_MINOR << 8) | VERSION_PATCH
}

/// Writes UTF-8 runtime information (version, CPU arch, OS) into `out_buf`.
#[no_mangle]
pub extern "C" fn ws_core_get_info(out_buf: *mut u8, out_cap: usize) -> i32 {
    let info = format!(
        "websystem_core v{}.{}.{} ({}/{}) [rustls+html5ever+taffy]",
        VERSION_MAJOR,
        VERSION_MINOR,
        VERSION_PATCH,
        env::consts::ARCH,
        env::consts::OS
    );
    write_utf8_to_buf(&info, out_buf, out_cap)
}

/// Executes a Stage 0 AppContainer smoke test (UTF-8 decode, heap allocation, FNV-1a hash).
#[no_mangle]
pub extern "C" fn ws_core_smoke_test(
    input_ptr: *const u8,
    input_len: usize,
    out_buf: *mut u8,
    out_cap: usize,
) -> i32 {
    if out_buf.is_null() || out_cap == 0 {
        return -1;
    }

    // SAFETY: Caller guarantees `input_ptr` validity for `input_len` bytes.
    let raw_input = unsafe { read_input_bytes(input_ptr, input_len) };
    let normalized = match str::from_utf8(raw_input) {
        Ok(s) => s.trim().to_string(),
        Err(_) => return -2,
    };

    let checksum = fnv1a_32(normalized.as_bytes());
    let char_count = normalized.chars().count();
    let report = format!(
        "OK | arch={} | chars={} | fnv1a=0x{:08X}",
        env::consts::ARCH,
        char_count,
        checksum
    );

    write_utf8_to_buf(&report, out_buf, out_cap)
}

/// Executes an HTTP/HTTPS request via `ureq` + `rustls` (TLS 1.3/1.2) and returns an owned response handle.
/// Caller must release the returned pointer using `ws_http_response_free`.
#[no_mangle]
pub extern "C" fn ws_http_request(
    method_ptr: *const u8,
    method_len: usize,
    url_ptr: *const u8,
    url_len: usize,
    ua_ptr: *const u8,
    ua_len: usize,
    body_ptr: *const u8,
    body_len: usize,
    timeout_secs: u32,
) -> *mut WsHttpResponse {
    // SAFETY: Caller guarantees valid pointers for the specified lengths during this synchronous call.
    let method_bytes = unsafe { read_input_bytes(method_ptr, method_len) };
    let url_bytes = unsafe { read_input_bytes(url_ptr, url_len) };
    let ua_bytes = unsafe { read_input_bytes(ua_ptr, ua_len) };
    let body_bytes = unsafe { read_input_bytes(body_ptr, body_len) };

    let method = str::from_utf8(method_bytes).unwrap_or("GET");
    let url = str::from_utf8(url_bytes).unwrap_or("");
    let ua = str::from_utf8(ua_bytes).unwrap_or("");

    let response = execute_http_request(method, url, ua, body_bytes, timeout_secs);
    Box::into_raw(Box::new(response))
}

#[no_mangle]
pub extern "C" fn ws_http_response_status(resp: *const WsHttpResponse) -> i32 {
    if resp.is_null() {
        return 0;
    }
    // SAFETY: Non-null handle created by `ws_http_request`.
    unsafe { (*resp).status_code }
}

#[no_mangle]
pub extern "C" fn ws_http_response_elapsed_ms(resp: *const WsHttpResponse) -> u64 {
    if resp.is_null() {
        return 0;
    }
    // SAFETY: Non-null handle created by `ws_http_request`.
    unsafe { (*resp).elapsed_ms }
}

#[no_mangle]
pub extern "C" fn ws_http_response_content_type(
    resp: *const WsHttpResponse,
    out_buf: *mut u8,
    out_cap: usize,
) -> i32 {
    if resp.is_null() {
        return -1;
    }
    // SAFETY: Non-null handle created by `ws_http_request`.
    let ct = unsafe { &(*resp).content_type };
    write_utf8_to_buf(ct, out_buf, out_cap)
}

#[no_mangle]
pub extern "C" fn ws_http_response_tls_info(
    resp: *const WsHttpResponse,
    out_buf: *mut u8,
    out_cap: usize,
) -> i32 {
    if resp.is_null() {
        return -1;
    }
    // SAFETY: Non-null handle created by `ws_http_request`.
    let info = unsafe { &(*resp).tls_info };
    write_utf8_to_buf(info, out_buf, out_cap)
}

#[no_mangle]
pub extern "C" fn ws_http_response_error(
    resp: *const WsHttpResponse,
    out_buf: *mut u8,
    out_cap: usize,
) -> i32 {
    if resp.is_null() {
        return -1;
    }
    // SAFETY: Non-null handle created by `ws_http_request`.
    let err = unsafe { &(*resp).error_message };
    write_utf8_to_buf(err, out_buf, out_cap)
}

#[no_mangle]
pub extern "C" fn ws_http_response_body_len(resp: *const WsHttpResponse) -> usize {
    if resp.is_null() {
        return 0;
    }
    // SAFETY: Non-null handle created by `ws_http_request`.
    unsafe { (*resp).body.len() }
}

#[no_mangle]
pub extern "C" fn ws_http_response_copy_body(
    resp: *const WsHttpResponse,
    out_buf: *mut u8,
    out_cap: usize,
) -> i32 {
    if resp.is_null() || out_buf.is_null() || out_cap == 0 {
        return -1;
    }
    // SAFETY: Non-null handle and caller-provided writable `out_buf` of `out_cap` bytes.
    let body = unsafe { &(*resp).body };
    let len = body.len().min(out_cap);
    let dst = unsafe { slice::from_raw_parts_mut(out_buf, out_cap) };
    dst[..len].copy_from_slice(&body[..len]);
    len as i32
}

#[no_mangle]
pub extern "C" fn ws_http_response_free(resp: *mut WsHttpResponse) {
    if !resp.is_null() {
        // SAFETY: Reclaims the `Box` originally allocated by `ws_http_request`.
        unsafe {
            drop(Box::from_raw(resp));
        }
    }
}

/// Parses HTML + CSS and computes Taffy layout into a binary Display List (`WSR1`).
/// Caller must release the returned pointer using `ws_render_buffer_free`.
#[no_mangle]
pub extern "C" fn ws_render_html(
    html_ptr: *const u8,
    html_len: usize,
    base_url_ptr: *const u8,
    base_url_len: usize,
    viewport_width: f32,
    dark_mode: u8,
) -> *mut WsRenderBuffer {
    // SAFETY: Caller guarantees valid input byte slices for this synchronous call.
    let html_bytes = unsafe { read_input_bytes(html_ptr, html_len) };
    let url_bytes = unsafe { read_input_bytes(base_url_ptr, base_url_len) };

    let html = String::from_utf8_lossy(html_bytes);
    let base_url = str::from_utf8(url_bytes).unwrap_or("");

    let buf = render_html_to_display_list(&html, base_url, viewport_width, dark_mode != 0);
    Box::into_raw(Box::new(buf))
}

#[no_mangle]
pub extern "C" fn ws_render_buffer_len(buf: *const WsRenderBuffer) -> usize {
    if buf.is_null() {
        return 0;
    }
    // SAFETY: Non-null handle created by `ws_render_html`.
    unsafe { (*buf).bytes.len() }
}

#[no_mangle]
pub extern "C" fn ws_render_buffer_copy(
    buf: *const WsRenderBuffer,
    out_buf: *mut u8,
    out_cap: usize,
) -> i32 {
    if buf.is_null() || out_buf.is_null() || out_cap == 0 {
        return -1;
    }
    // SAFETY: Non-null handle and caller-provided writable `out_buf` of `out_cap` bytes.
    let data = unsafe { &(*buf).bytes };
    let len = data.len().min(out_cap);
    let dst = unsafe { slice::from_raw_parts_mut(out_buf, out_cap) };
    dst[..len].copy_from_slice(&data[..len]);
    len as i32
}

#[no_mangle]
pub extern "C" fn ws_render_buffer_free(buf: *mut WsRenderBuffer) {
    if !buf.is_null() {
        // SAFETY: Reclaims the `Box` originally allocated by `ws_render_html`.
        unsafe {
            drop(Box::from_raw(buf));
        }
    }
}
