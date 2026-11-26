use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};
use ureq::tls::{RootCerts, TlsConfig, TlsProvider};
use ureq::{http, Agent, Error, ResponseExt};

const DEFAULT_USER_AGENT: &str =
    "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36";
const MAX_RESPONSE_BODY_BYTES: u64 = 8 * 1024 * 1024; // 8 MB cap for 1 GB RAM Lumia safety

pub struct WsHttpResponse {
    pub status_code: i32,
    pub elapsed_ms: u64,
    pub content_type: String,
    pub tls_info: String,
    pub final_url: String,
    pub error_message: String,
    pub body: Vec<u8>,
}

fn shared_agent() -> &'static Agent {
    static AGENT: OnceLock<Agent> = OnceLock::new();
    AGENT.get_or_init(|| {
        let crypto = Arc::new(rustls_rustcrypto::provider());
        let tls = TlsConfig::builder()
            .provider(TlsProvider::Rustls)
            .unversioned_rustls_crypto_provider(crypto)
            .root_certs(RootCerts::WebPki)
            .build();

        Agent::config_builder()
            .tls_config(tls)
            .http_status_as_error(false)
            .max_redirects(10)
            .build()
            .new_agent()
    })
}

fn classify_ureq_error(err: &Error) -> (&'static str, String) {
    match err {
        Error::Timeout(kind) => ("Timeout", format!("Hết thời gian chờ ({:?})", kind)),
        Error::HostNotFound => ("DnsResolutionFailed", "Không phân giải được tên miền (DNS)".to_string()),
        Error::ConnectionFailed => ("NetworkUnavailable", "Không kết nối được tới máy chủ".to_string()),
        Error::Tls(msg) => ("SslHandshakeFailed", format!("Lỗi bắt tay TLS: {}", msg)),
        Error::TooManyRedirects => ("HttpError", "Quá số lần chuyển hướng (redirect)".to_string()),
        Error::BodyExceedsLimit(limit) => (
            "HttpError",
            format!("Nội dung vượt quá giới hạn {} bytes", limit),
        ),
        other => ("Unknown", other.to_string()),
    }
}

fn error_response(
    status_code: i32,
    start: Instant,
    content_type: String,
    tls_info: &str,
    final_url: String,
    error_message: String,
) -> WsHttpResponse {
    WsHttpResponse {
        status_code,
        elapsed_ms: start.elapsed().as_millis() as u64,
        content_type,
        tls_info: tls_info.to_string(),
        final_url,
        error_message,
        body: Vec::new(),
    }
}

fn run_with_timeout<B: ureq::AsSendBody>(
    agent: &Agent,
    req: http::Request<B>,
    timeout_secs: u64,
) -> Result<http::Response<ureq::Body>, Error> {
    let configured = agent
        .configure_request(req)
        .timeout_global(Some(Duration::from_secs(timeout_secs)))
        .build();
    agent.run(configured)
}

pub fn execute_http_request(
    method: &str,
    url: &str,
    user_agent: &str,
    content_type_req: &str,
    body: &[u8],
    timeout_secs: u32,
    max_body_bytes: u32,
) -> WsHttpResponse {
    let start = Instant::now();
    let trimmed_url = url.trim();
    let method_norm = if method.trim().is_empty() {
        "GET"
    } else {
        method.trim()
    };
    let ua = if user_agent.trim().is_empty() {
        DEFAULT_USER_AGENT
    } else {
        user_agent.trim()
    };

    let req_body: Vec<u8> = if method_norm.eq_ignore_ascii_case("POST")
        || method_norm.eq_ignore_ascii_case("PUT")
        || method_norm.eq_ignore_ascii_case("PATCH")
    {
        body.to_vec()
    } else {
        Vec::new()
    };

    let mut builder = http::Request::builder()
        .method(method_norm)
        .uri(trimmed_url)
        .header("User-Agent", ua)
        .header("Accept", "*/*");

    if !req_body.is_empty() {
        let req_ct = if content_type_req.trim().is_empty() {
            "application/json"
        } else {
            content_type_req.trim()
        };
        builder = builder.header("Content-Type", req_ct);
    }

    let agent = shared_agent();
    let clamped_timeout = timeout_secs.clamp(5, 60) as u64;
    let built_req = if req_body.is_empty() {
        builder.body(Vec::new())
    } else {
        builder.body(req_body)
    };

    let req = match built_req {
        Ok(r) => r,
        Err(err) => {
            return error_response(
                0,
                start,
                String::new(),
                "InvalidRequest",
                trimmed_url.to_string(),
                format!("URL hoặc request không hợp lệ: {}", err),
            );
        }
    };

    let body_limit = if max_body_bytes == 0 {
        MAX_RESPONSE_BODY_BYTES
    } else {
        (max_body_bytes as u64).clamp(1024, MAX_RESPONSE_BODY_BYTES)
    };

    match run_with_timeout(agent, req, clamped_timeout) {
        Ok(mut response) => {
            let status_code = response.status().as_u16() as i32;
            let final_url = response.get_uri().to_string();
            let tls_label = if final_url.starts_with("https://") {
                "rustls (TLS 1.3/1.2 + Cookies)".to_string()
            } else {
                "Plain HTTP (ureq + Cookies)".to_string()
            };
            let content_type = response
                .headers()
                .get("content-type")
                .and_then(|v| v.to_str().ok())
                .unwrap_or("text/plain")
                .to_string();

            match response
                .body_mut()
                .with_config()
                .limit(body_limit)
                .read_to_vec()
            {
                Ok(bytes) => WsHttpResponse {
                    status_code,
                    elapsed_ms: start.elapsed().as_millis() as u64,
                    content_type,
                    tls_info: tls_label,
                    final_url,
                    error_message: String::new(),
                    body: bytes,
                },
                Err(err) => {
                    let (kind, msg) = classify_ureq_error(&err);
                    error_response(status_code, start, content_type, kind, final_url, msg)
                }
            }
        }
        Err(err) => {
            let (kind, msg) = classify_ureq_error(&err);
            error_response(
                0,
                start,
                String::new(),
                kind,
                trimmed_url.to_string(),
                msg,
            )
        }
    }
}
