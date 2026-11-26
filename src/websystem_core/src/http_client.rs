use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};
use ureq::tls::{RootCerts, TlsConfig, TlsProvider};
use ureq::{http, Agent, Error};

const DEFAULT_USER_AGENT: &str =
    "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36";
const MAX_RESPONSE_BODY_BYTES: u64 = 8 * 1024 * 1024; // 8 MB cap for 1 GB RAM Lumia safety

pub struct WsHttpResponse {
    pub status_code: i32,
    pub elapsed_ms: u64,
    pub content_type: String,
    pub tls_info: String,
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
    body: &[u8],
    timeout_secs: u32,
) -> WsHttpResponse {
    let start = Instant::now();
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
        .uri(url.trim())
        .header("User-Agent", ua)
        .header("Accept", "*/*");

    if !req_body.is_empty() {
        builder = builder.header("Content-Type", "application/json");
    }

    let agent = shared_agent();
    let clamped_timeout = timeout_secs.clamp(5, 60) as u64;
    let run_result = if req_body.is_empty() {
        match builder.body(()) {
            Ok(req) => run_with_timeout(agent, req, clamped_timeout),
            Err(err) => {
                return WsHttpResponse {
                    status_code: 0,
                    elapsed_ms: start.elapsed().as_millis() as u64,
                    content_type: String::new(),
                    tls_info: "InvalidRequest".to_string(),
                    error_message: format!("URL hoặc request không hợp lệ: {}", err),
                    body: Vec::new(),
                };
            }
        }
    } else {
        match builder.body(req_body) {
            Ok(req) => run_with_timeout(agent, req, clamped_timeout),
            Err(err) => {
                return WsHttpResponse {
                    status_code: 0,
                    elapsed_ms: start.elapsed().as_millis() as u64,
                    content_type: String::new(),
                    tls_info: "InvalidRequest".to_string(),
                    error_message: format!("URL hoặc request không hợp lệ: {}", err),
                    body: Vec::new(),
                };
            }
        }
    };

    let tls_label = if url.trim().starts_with("https://") {
        "rustls (TLS 1.3/1.2 + RustCrypto + WebPKI)".to_string()
    } else {
        "Plain HTTP (ureq)".to_string()
    };

    match run_result {
        Ok(mut response) => {
            let status_code = response.status().as_u16() as i32;
            let content_type = response
                .headers()
                .get("content-type")
                .and_then(|v| v.to_str().ok())
                .unwrap_or("text/plain")
                .to_string();

            match response
                .body_mut()
                .with_config()
                .limit(MAX_RESPONSE_BODY_BYTES)
                .read_to_vec()
            {
                Ok(bytes) => WsHttpResponse {
                    status_code,
                    elapsed_ms: start.elapsed().as_millis() as u64,
                    content_type,
                    tls_info: tls_label,
                    error_message: String::new(),
                    body: bytes,
                },
                Err(err) => {
                    let (kind, msg) = classify_ureq_error(&err);
                    WsHttpResponse {
                        status_code,
                        elapsed_ms: start.elapsed().as_millis() as u64,
                        content_type,
                        tls_info: kind.to_string(),
                        error_message: msg,
                        body: Vec::new(),
                    }
                }
            }
        }
        Err(err) => {
            let (kind, msg) = classify_ureq_error(&err);
            WsHttpResponse {
                status_code: 0,
                elapsed_ms: start.elapsed().as_millis() as u64,
                content_type: String::new(),
                tls_info: kind.to_string(),
                error_message: msg,
                body: Vec::new(),
            }
        }
    }
}
