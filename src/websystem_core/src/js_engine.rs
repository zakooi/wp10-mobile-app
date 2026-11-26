use boa_engine::object::ObjectInitializer;
use boa_engine::{js_string, Context, JsResult, JsString, JsValue, NativeFunction, Source};
use scraper::{Html, Node, Selector};
use std::cell::RefCell;

const MAX_DOM_NODES: usize = 6000;
const MAX_TIMERS_PER_PAGE: usize = 48;
const SCRIPT_CHUNK_SEPARATOR: &str = "\n/*__WS_SCRIPT_SEP__*/\n";

#[derive(Clone)]
enum DomKind {
    Document,
    Element {
        tag: String,
        attrs: Vec<(String, String)>,
    },
    Text(String),
    Comment(String),
}

struct DomNode {
    kind: DomKind,
    parent: Option<usize>,
    children: Vec<usize>,
}

struct DomState {
    nodes: Vec<DomNode>,
    body_id: Option<usize>,
    head_id: Option<usize>,
    doc_el_id: Option<usize>,
    title: String,
    base_url: String,
    user_agent: String,
    dark_mode: bool,
    mutated: bool,
    doc_writes: Vec<String>,
    pending_timers: Vec<JsValue>,
    dom_listeners: Vec<JsValue>,
    console_logs: Vec<String>,
}

thread_local! {
    static ACTIVE_DOM: RefCell<Option<DomState>> = const { RefCell::new(None) };
}

struct ActiveDomGuard;

impl Drop for ActiveDomGuard {
    fn drop(&mut self) {
        ACTIVE_DOM.with(|cell| {
            *cell.borrow_mut() = None;
        });
    }
}

fn with_dom<F, R>(f: F) -> R
where
    F: FnOnce(&mut DomState) -> R,
{
    ACTIVE_DOM.with(|cell| {
        let mut guard = cell.borrow_mut();
        let state = guard.as_mut().expect("ACTIVE_DOM not initialized");
        f(state)
    })
}

impl DomState {
    fn from_html(html: &str, base_url: &str, user_agent: &str, dark_mode: bool) -> (Self, Vec<String>) {
        let doc = Html::parse_document(html);
        let mut state = Self {
            nodes: Vec::with_capacity(256),
            body_id: None,
            head_id: None,
            doc_el_id: None,
            title: String::new(),
            base_url: base_url.to_string(),
            user_agent: user_agent.to_string(),
            dark_mode,
            mutated: false,
            doc_writes: Vec::new(),
            pending_timers: Vec::new(),
            dom_listeners: Vec::new(),
            console_logs: Vec::new(),
        };

        state.nodes.push(DomNode {
            kind: DomKind::Document,
            parent: None,
            children: Vec::new(),
        });

        let mut inline_scripts = Vec::new();
        for child in doc.tree.root().children() {
            state.import_ego_node(child, 0, &mut inline_scripts);
        }

        if let Some(title_id) = state.find_first_tag(0, "title") {
            state.title = state.collect_text(title_id).trim().to_string();
        }
        if state.doc_el_id.is_none() {
            state.doc_el_id = state.find_first_tag(0, "html");
        }
        if state.head_id.is_none() {
            state.head_id = state.find_first_tag(0, "head");
        }
        if state.body_id.is_none() {
            state.body_id = state.find_first_tag(0, "body");
        }

        (state, inline_scripts)
    }

    fn import_ego_node(
        &mut self,
        node_ref: ego_tree::NodeRef<'_, Node>,
        parent_id: usize,
        inline_scripts: &mut Vec<String>,
    ) {
        if self.nodes.len() >= MAX_DOM_NODES {
            return;
        }
        match node_ref.value() {
            Node::Document => {
                for ch in node_ref.children() {
                    self.import_ego_node(ch, parent_id, inline_scripts);
                }
            }
            Node::Element(el) => {
                let tag = el.name().to_ascii_lowercase();
                let attrs: Vec<(String, String)> = el
                    .attrs()
                    .map(|(k, v)| (k.to_ascii_lowercase(), v.to_string()))
                    .collect();

                let is_exec_script = if tag == "script" {
                    let has_src = attrs.iter().any(|(k, v)| k == "src" && !v.trim().is_empty());
                    let ty = attrs
                        .iter()
                        .find(|(k, _)| k == "type")
                        .map(|(_, v)| v.trim().to_ascii_lowercase())
                        .unwrap_or_default();
                    let runnable_type = ty.is_empty()
                        || ty == "text/javascript"
                        || ty == "application/javascript"
                        || ty == "module";
                    !has_src && runnable_type
                } else {
                    false
                };

                let nid = self.alloc_node(
                    DomKind::Element {
                        tag: tag.clone(),
                        attrs,
                    },
                    Some(parent_id),
                );
                self.nodes[parent_id].children.push(nid);

                if tag == "html" && self.doc_el_id.is_none() {
                    self.doc_el_id = Some(nid);
                } else if tag == "head" && self.head_id.is_none() {
                    self.head_id = Some(nid);
                } else if tag == "body" && self.body_id.is_none() {
                    self.body_id = Some(nid);
                }

                for ch in node_ref.children() {
                    self.import_ego_node(ch, nid, inline_scripts);
                }

                if is_exec_script {
                    let code = self.collect_text(nid);
                    if !code.trim().is_empty() {
                        inline_scripts.push(code);
                    }
                }
            }
            Node::Text(t) => {
                let nid = self.alloc_node(DomKind::Text(t.to_string()), Some(parent_id));
                self.nodes[parent_id].children.push(nid);
            }
            Node::Comment(c) => {
                let nid = self.alloc_node(DomKind::Comment(c.to_string()), Some(parent_id));
                self.nodes[parent_id].children.push(nid);
            }
            _ => {}
        }
    }

    fn alloc_node(&mut self, kind: DomKind, parent: Option<usize>) -> usize {
        let id = self.nodes.len();
        self.nodes.push(DomNode {
            kind,
            parent,
            children: Vec::new(),
        });
        id
    }

    fn find_first_tag(&self, root: usize, target_tag: &str) -> Option<usize> {
        let node = self.nodes.get(root)?;
        if let DomKind::Element { tag, .. } = &node.kind {
            if tag == target_tag {
                return Some(root);
            }
        }
        for &ch in &node.children {
            if let Some(found) = self.find_first_tag(ch, target_tag) {
                return Some(found);
            }
        }
        None
    }

    fn collect_text(&self, root: usize) -> String {
        let mut out = String::new();
        self.collect_text_rec(root, &mut out);
        out
    }

    fn collect_text_rec(&self, id: usize, out: &mut String) {
        let Some(node) = self.nodes.get(id) else {
            return;
        };
        match &node.kind {
            DomKind::Text(t) => out.push_str(t),
            DomKind::Element { tag, .. } => {
                if tag == "script" || tag == "style" {
                    for &ch in &node.children {
                        if let Some(DomNode {
                            kind: DomKind::Text(t),
                            ..
                        }) = self.nodes.get(ch)
                        {
                            out.push_str(t);
                        }
                    }
                } else {
                    for &ch in &node.children {
                        self.collect_text_rec(ch, out);
                    }
                }
            }
            _ => {
                for &ch in &node.children {
                    self.collect_text_rec(ch, out);
                }
            }
        }
    }

    fn get_attr(&self, id: usize, key: &str) -> Option<String> {
        let k_low = key.to_ascii_lowercase();
        let node = self.nodes.get(id)?;
        if let DomKind::Element { attrs, .. } = &node.kind {
            attrs
                .iter()
                .find(|(k, _)| k == &k_low)
                .map(|(_, v)| v.clone())
        } else {
            None
        }
    }

    fn set_attr(&mut self, id: usize, key: &str, val: &str) {
        let k_low = key.to_ascii_lowercase();
        if let Some(DomNode {
            kind: DomKind::Element { attrs, .. },
            ..
        }) = self.nodes.get_mut(id)
        {
            if let Some((_, existing)) = attrs.iter_mut().find(|(k, _)| k == &k_low) {
                *existing = val.to_string();
            } else {
                attrs.push((k_low, val.to_string()));
            }
            self.mutated = true;
        }
    }

    fn remove_attr(&mut self, id: usize, key: &str) {
        let k_low = key.to_ascii_lowercase();
        if let Some(DomNode {
            kind: DomKind::Element { attrs, .. },
            ..
        }) = self.nodes.get_mut(id)
        {
            let before = attrs.len();
            attrs.retain(|(k, _)| k != &k_low);
            if attrs.len() != before {
                self.mutated = true;
            }
        }
    }

    fn detach_node(&mut self, child_id: usize) {
        if child_id == 0 || child_id >= self.nodes.len() {
            return;
        }
        if let Some(old_parent) = self.nodes[child_id].parent.take() {
            if let Some(p_node) = self.nodes.get_mut(old_parent) {
                p_node.children.retain(|&c| c != child_id);
            }
        }
    }

    fn append_child(&mut self, parent_id: usize, child_id: usize) {
        if parent_id >= self.nodes.len() || child_id == 0 || child_id >= self.nodes.len() {
            return;
        }
        if parent_id == child_id {
            return;
        }
        self.detach_node(child_id);
        self.nodes[child_id].parent = Some(parent_id);
        self.nodes[parent_id].children.push(child_id);
        self.mutated = true;
    }

    fn set_text_content(&mut self, id: usize, text: &str) {
        if id >= self.nodes.len() {
            return;
        }
        match &mut self.nodes[id].kind {
            DomKind::Text(t) => {
                *t = text.to_string();
                self.mutated = true;
            }
            DomKind::Element { .. } => {
                self.nodes[id].children.clear();
                if !text.is_empty() && self.nodes.len() < MAX_DOM_NODES {
                    let tid = self.alloc_node(DomKind::Text(text.to_string()), Some(id));
                    self.nodes[id].children.push(tid);
                }
                self.mutated = true;
            }
            _ => {}
        }
    }

    fn set_inner_html(&mut self, id: usize, fragment_html: &str) {
        if id >= self.nodes.len() {
            return;
        }
        if !matches!(self.nodes[id].kind, DomKind::Element { .. }) {
            return;
        }
        self.nodes[id].children.clear();
        let frag = Html::parse_fragment(fragment_html);
        let mut ignored = Vec::new();
        for ch in frag.tree.root().children() {
            self.import_ego_node(ch, id, &mut ignored);
        }
        self.mutated = true;
    }

    fn matches_simple_selector(&self, id: usize, token: &str) -> bool {
        let Some(DomNode {
            kind: DomKind::Element { tag, attrs },
            ..
        }) = self.nodes.get(id)
        else {
            return false;
        };
        let t = token.trim();
        if t.is_empty() {
            return false;
        }
        if t == "*" {
            return true;
        }

        // Handle attribute selector e.g. [name="q"] or input[type=text]
        let (base, attr_cond) = if let Some(b_start) = t.find('[') {
            if let Some(b_end) = t[b_start..].find(']') {
                let cond = &t[b_start + 1..b_start + b_end];
                (&t[..b_start], Some(cond))
            } else {
                (t, None)
            }
        } else {
            (t, None)
        };

        if let Some(cond) = attr_cond {
            if let Some((ak, av)) = cond.split_once('=') {
                let ak_low = ak.trim().to_ascii_lowercase();
                let av_clean = av.trim().trim_matches('"').trim_matches('\'');
                if !attrs
                    .iter()
                    .any(|(k, v)| k == &ak_low && v == av_clean)
                {
                    return false;
                }
            } else {
                let ak_low = cond.trim().to_ascii_lowercase();
                if !attrs.iter().any(|(k, _)| k == &ak_low) {
                    return false;
                }
            }
        }

        if base.is_empty() {
            return true;
        }

        // Parse tag#id.cls1.cls2
        let mut rest = base;
        let mut req_tag: Option<&str> = None;
        let mut req_id: Option<&str> = None;
        let mut req_classes: Vec<&str> = Vec::new();

        if !rest.starts_with('#') && !rest.starts_with('.') {
            let cut = rest.find(['#', '.']).unwrap_or(rest.len());
            req_tag = Some(&rest[..cut]);
            rest = &rest[cut..];
        }
        while !rest.is_empty() {
            if let Some(after) = rest.strip_prefix('#') {
                let cut = after.find(['#', '.']).unwrap_or(after.len());
                req_id = Some(&after[..cut]);
                rest = &after[cut..];
            } else if let Some(after) = rest.strip_prefix('.') {
                let cut = after.find(['#', '.']).unwrap_or(after.len());
                req_classes.push(&after[..cut]);
                rest = &after[cut..];
            } else {
                break;
            }
        }

        if let Some(rt) = req_tag {
            if !tag.eq_ignore_ascii_case(rt) {
                return false;
            }
        }
        if let Some(ri) = req_id {
            if !attrs.iter().any(|(k, v)| k == "id" && v == ri) {
                return false;
            }
        }
        if !req_classes.is_empty() {
            let cls_str = attrs
                .iter()
                .find(|(k, _)| k == "class")
                .map(|(_, v)| v.as_str())
                .unwrap_or("");
            for rc in req_classes {
                if !cls_str.split_whitespace().any(|c| c == rc) {
                    return false;
                }
            }
        }
        true
    }

    fn matches_selector_chain(&self, id: usize, selector: &str) -> bool {
        let parts: Vec<&str> = selector.split_whitespace().collect();
        if parts.is_empty() {
            return false;
        }
        let Some(last) = parts.last() else {
            return false;
        };
        if !self.matches_simple_selector(id, last) {
            return false;
        }
        let mut cur_id = id;
        for &ancestor_sel in parts[..parts.len() - 1].iter().rev() {
            let mut matched = false;
            let mut p = self.nodes.get(cur_id).and_then(|n| n.parent);
            while let Some(pid) = p {
                if self.matches_simple_selector(pid, ancestor_sel) {
                    cur_id = pid;
                    matched = true;
                    break;
                }
                p = self.nodes.get(pid).and_then(|n| n.parent);
            }
            if !matched {
                return false;
            }
        }
        true
    }

    fn query_all(&self, root: usize, selector_list: &str, limit: usize) -> Vec<usize> {
        let selectors: Vec<&str> = selector_list
            .split(',')
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .collect();
        if selectors.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        self.query_rec(root, root, &selectors, limit, &mut out);
        out
    }

    fn query_rec(
        &self,
        cur: usize,
        root: usize,
        selectors: &[&str],
        limit: usize,
        out: &mut Vec<usize>,
    ) {
        if out.len() >= limit {
            return;
        }
        if cur != root && selectors.iter().any(|s| self.matches_selector_chain(cur, s)) {
            out.push(cur);
            if out.len() >= limit {
                return;
            }
        }
        if let Some(node) = self.nodes.get(cur) {
            for &ch in &node.children {
                self.query_rec(ch, root, selectors, limit, out);
                if out.len() >= limit {
                    return;
                }
            }
        }
    }

    fn get_by_id(&self, target_id: &str) -> Option<usize> {
        for (idx, node) in self.nodes.iter().enumerate() {
            if let DomKind::Element { attrs, .. } = &node.kind {
                if attrs.iter().any(|(k, v)| k == "id" && v == target_id) {
                    return Some(idx);
                }
            }
        }
        None
    }

    fn serialize_children_html(&self, id: usize) -> String {
        let mut out = String::new();
        if let Some(node) = self.nodes.get(id) {
            for &ch in &node.children {
                self.serialize_node(ch, &mut out, false);
            }
        }
        out
    }

    fn serialize_document(&mut self) -> String {
        if !self.doc_writes.is_empty() {
            let combined = self.doc_writes.join("");
            if let Some(body) = self.body_id {
                let frag = Html::parse_fragment(&combined);
                let mut ignored = Vec::new();
                for ch in frag.tree.root().children() {
                    self.import_ego_node(ch, body, &mut ignored);
                }
            } else {
                let frag = Html::parse_fragment(&combined);
                let mut ignored = Vec::new();
                for ch in frag.tree.root().children() {
                    self.import_ego_node(ch, 0, &mut ignored);
                }
            }
            self.mutated = true;
        }

        let mut out = String::with_capacity(4096);
        out.push_str("<!DOCTYPE html>");
        if let Some(root) = self.nodes.first() {
            for &ch in &root.children {
                self.serialize_node(ch, &mut out, false);
            }
        }
        out
    }

    fn serialize_node(&self, id: usize, out: &mut String, raw_text: bool) {
        let Some(node) = self.nodes.get(id) else {
            return;
        };
        match &node.kind {
            DomKind::Document => {
                for &ch in &node.children {
                    self.serialize_node(ch, out, false);
                }
            }
            DomKind::Comment(_) => {}
            DomKind::Text(t) => {
                if raw_text {
                    out.push_str(t);
                } else {
                    push_escaped_html(out, t);
                }
            }
            DomKind::Element { tag, attrs } => {
                out.push('<');
                out.push_str(tag);
                for (k, v) in attrs {
                    out.push(' ');
                    out.push_str(k);
                    out.push_str("=\"");
                    push_escaped_attr(out, v);
                    out.push('"');
                }
                out.push('>');
                if is_void_tag(tag) {
                    return;
                }
                let child_raw = tag == "script" || tag == "style";
                for &ch in &node.children {
                    self.serialize_node(ch, out, child_raw);
                }
                out.push_str("</");
                out.push_str(tag);
                out.push('>');
            }
        }
    }
}

fn is_void_tag(tag: &str) -> bool {
    matches!(
        tag,
        "area" | "base" | "br" | "col" | "embed" | "hr" | "img" | "input" | "link" | "meta"
            | "param" | "source" | "track" | "wbr"
    )
}

fn push_escaped_html(out: &mut String, s: &str) {
    for ch in s.chars() {
        match ch {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            _ => out.push(ch),
        }
    }
}

fn push_escaped_attr(out: &mut String, s: &str) {
    for ch in s.chars() {
        match ch {
            '&' => out.push_str("&amp;"),
            '"' => out.push_str("&quot;"),
            _ => out.push(ch),
        }
    }
}

fn resolve_script_url(base_url: &str, src: &str) -> String {
    let h = src.trim();
    if h.is_empty()
        || h.starts_with("http://")
        || h.starts_with("https://")
        || h.starts_with("data:")
    {
        return h.to_string();
    }
    let Some(scheme_idx) = base_url.find("://") else {
        return h.to_string();
    };
    let scheme = &base_url[..scheme_idx];
    let after_scheme = &base_url[scheme_idx + 3..];
    let authority = after_scheme.split('/').next().unwrap_or("");
    if h.starts_with("//") {
        return format!("{}:{}", scheme, h);
    }
    if h.starts_with('/') {
        return format!("{}://{}{}", scheme, authority, h);
    }
    let base_no_query = base_url.split('?').next().unwrap_or(base_url);
    if let Some(last_slash) = base_no_query.rfind('/') {
        if last_slash >= scheme_idx + 3 {
            return format!("{}/{}", &base_no_query[..last_slash], h);
        }
    }
    format!("{}://{}/{}", scheme, authority, h)
}

pub fn extract_script_urls(html: &str, base_url: &str) -> Vec<String> {
    let doc = Html::parse_document(html);
    let mut urls = Vec::new();
    let Ok(sel) = Selector::parse("script[src]") else {
        return urls;
    };
    for el in doc.select(&sel) {
        let ty = el
            .value()
            .attr("type")
            .unwrap_or("")
            .trim()
            .to_ascii_lowercase();
        if !ty.is_empty()
            && ty != "text/javascript"
            && ty != "application/javascript"
            && ty != "module"
        {
            continue;
        }
        if let Some(src) = el.value().attr("src") {
            let resolved = resolve_script_url(base_url, src);
            if (resolved.starts_with("http://") || resolved.starts_with("https://"))
                && !urls.iter().any(|u| u == &resolved)
            {
                urls.push(resolved);
                if urls.len() >= 4 {
                    break;
                }
            }
        }
    }
    urls
}

pub struct JsExecutionResult {
    pub html: String,
    pub scripts_executed: usize,
    pub errors: usize,
}

pub fn execute_page_scripts(
    html: &str,
    base_url: &str,
    extra_js: &str,
    user_agent: &str,
    dark_mode: bool,
) -> JsExecutionResult {
    let (dom_state, inline_scripts) = DomState::from_html(html, base_url, user_agent, dark_mode);
    let mut external_scripts: Vec<String> = Vec::new();
    if !extra_js.trim().is_empty() {
        for chunk in extra_js.split(SCRIPT_CHUNK_SEPARATOR) {
            if !chunk.trim().is_empty() {
                external_scripts.push(chunk.to_string());
            }
        }
    }

    if inline_scripts.is_empty() && external_scripts.is_empty() {
        return JsExecutionResult {
            html: html.to_string(),
            scripts_executed: 0,
            errors: 0,
        };
    }

    let mut ctx = Context::default();
    ctx.runtime_limits_mut().set_loop_iteration_limit(250_000);
    ctx.runtime_limits_mut().set_recursion_limit(64);

    ACTIVE_DOM.with(|cell| {
        *cell.borrow_mut() = Some(dom_state);
    });
    let _guard = ActiveDomGuard;

    if register_host_bindings(&mut ctx).is_err() {
        return JsExecutionResult {
            html: html.to_string(),
            scripts_executed: 0,
            errors: 1,
        };
    }

    if ctx.eval(Source::from_bytes(JS_DOM_BOOTSTRAP)).is_err() {
        return JsExecutionResult {
            html: html.to_string(),
            scripts_executed: 0,
            errors: 1,
        };
    }

    let mut scripts_executed = 0usize;
    let mut errors = 0usize;

    for script in external_scripts.iter().chain(inline_scripts.iter()) {
        scripts_executed += 1;
        if ctx.eval(Source::from_bytes(script.as_bytes())).is_err() {
            errors += 1;
        }
    }

    // Flush DOMContentLoaded listeners and queued setTimeout callbacks
    let listeners: Vec<JsValue> = with_dom(|d| std::mem::take(&mut d.dom_listeners));
    for listener in listeners {
        if let Some(callable) = listener.as_callable() {
            if callable.call(&JsValue::undefined(), &[], &mut ctx).is_err() {
                errors += 1;
            }
        }
    }

    for _ in 0..3 {
        let timers: Vec<JsValue> = with_dom(|d| std::mem::take(&mut d.pending_timers));
        if timers.is_empty() {
            break;
        }
        for cb in timers {
            if let Some(callable) = cb.as_callable() {
                if callable.call(&JsValue::undefined(), &[], &mut ctx).is_err() {
                    errors += 1;
                }
            }
        }
    }

    let final_html = with_dom(|d| {
        if d.mutated || !d.doc_writes.is_empty() {
            d.serialize_document()
        } else {
            html.to_string()
        }
    });

    JsExecutionResult {
        html: final_html,
        scripts_executed,
        errors,
    }
}

fn arg_str(args: &[JsValue], idx: usize, ctx: &mut Context) -> String {
    args.get(idx)
        .and_then(|v| v.to_string(ctx).ok())
        .map(|s| s.to_std_string_escaped())
        .unwrap_or_default()
}

fn arg_id(args: &[JsValue], idx: usize, ctx: &mut Context) -> Option<usize> {
    let val = args.get(idx)?;
    let n = val.to_i32(ctx).ok()?;
    if n >= 0 {
        Some(n as usize)
    } else {
        None
    }
}

fn host_get_by_id(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let id_str = arg_str(args, 0, ctx);
    let res = with_dom(|d| d.get_by_id(&id_str));
    Ok(res.map(|id| JsValue::new(id as i32)).unwrap_or_else(JsValue::null))
}

fn host_qs(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let root = arg_id(args, 0, ctx).unwrap_or(0);
    let sel = arg_str(args, 1, ctx);
    let res = with_dom(|d| d.query_all(root, &sel, 1).into_iter().next());
    Ok(res.map(|id| JsValue::new(id as i32)).unwrap_or_else(JsValue::null))
}

fn host_qsa_ids(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let root = arg_id(args, 0, ctx).unwrap_or(0);
    let sel = arg_str(args, 1, ctx);
    let joined = with_dom(|d| {
        d.query_all(root, &sel, 256)
            .into_iter()
            .map(|id| id.to_string())
            .collect::<Vec<_>>()
            .join(",")
    });
    Ok(JsValue::from(JsString::from(joined.as_str())))
}

fn host_create_el(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let tag = arg_str(args, 0, ctx).trim().to_ascii_lowercase();
    let res = with_dom(|d| {
        if d.nodes.len() >= MAX_DOM_NODES || tag.is_empty() {
            return None;
        }
        d.mutated = true;
        Some(d.alloc_node(
            DomKind::Element {
                tag,
                attrs: Vec::new(),
            },
            None,
        ))
    });
    Ok(res.map(|id| JsValue::new(id as i32)).unwrap_or_else(JsValue::null))
}

fn host_create_text(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let txt = arg_str(args, 0, ctx);
    let res = with_dom(|d| {
        if d.nodes.len() >= MAX_DOM_NODES {
            return None;
        }
        d.mutated = true;
        Some(d.alloc_node(DomKind::Text(txt), None))
    });
    Ok(res.map(|id| JsValue::new(id as i32)).unwrap_or_else(JsValue::null))
}

fn host_get_attr(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::null());
    };
    let key = arg_str(args, 1, ctx);
    let res = with_dom(|d| d.get_attr(id, &key));
    Ok(res
        .map(|v| JsValue::from(JsString::from(v.as_str())))
        .unwrap_or_else(JsValue::null))
}

fn host_set_attr(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(id) = arg_id(args, 0, ctx) {
        let key = arg_str(args, 1, ctx);
        let val = arg_str(args, 2, ctx);
        with_dom(|d| d.set_attr(id, &key, &val));
    }
    Ok(JsValue::undefined())
}

fn host_rm_attr(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(id) = arg_id(args, 0, ctx) {
        let key = arg_str(args, 1, ctx);
        with_dom(|d| d.remove_attr(id, &key));
    }
    Ok(JsValue::undefined())
}

fn host_get_text(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::from(js_string!("")));
    };
    let txt = with_dom(|d| d.collect_text(id));
    Ok(JsValue::from(JsString::from(txt.as_str())))
}

fn host_set_text(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(id) = arg_id(args, 0, ctx) {
        let txt = arg_str(args, 1, ctx);
        with_dom(|d| d.set_text_content(id, &txt));
    }
    Ok(JsValue::undefined())
}

fn host_get_html(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::from(js_string!("")));
    };
    let out = with_dom(|d| d.serialize_children_html(id));
    Ok(JsValue::from(JsString::from(out.as_str())))
}

fn host_set_html(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(id) = arg_id(args, 0, ctx) {
        let frag = arg_str(args, 1, ctx);
        with_dom(|d| d.set_inner_html(id, &frag));
    }
    Ok(JsValue::undefined())
}

fn host_append(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let (Some(pid), Some(cid)) = (arg_id(args, 0, ctx), arg_id(args, 1, ctx)) {
        with_dom(|d| d.append_child(pid, cid));
    }
    Ok(JsValue::undefined())
}

fn host_remove(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(id) = arg_id(args, 0, ctx) {
        with_dom(|d| {
            d.detach_node(id);
            d.mutated = true;
        });
    }
    Ok(JsValue::undefined())
}

fn host_tag(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::from(js_string!("")));
    };
    let tag = with_dom(|d| match d.nodes.get(id).map(|n| &n.kind) {
        Some(DomKind::Element { tag, .. }) => tag.to_ascii_uppercase(),
        _ => String::new(),
    });
    Ok(JsValue::from(JsString::from(tag.as_str())))
}

fn host_children_ids(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::from(js_string!("")));
    };
    let joined = with_dom(|d| {
        d.nodes
            .get(id)
            .map(|n| {
                n.children
                    .iter()
                    .filter(|&&cid| matches!(d.nodes.get(cid).map(|c| &c.kind), Some(DomKind::Element { .. })))
                    .map(|cid| cid.to_string())
                    .collect::<Vec<_>>()
                    .join(",")
            })
            .unwrap_or_default()
    });
    Ok(JsValue::from(JsString::from(joined.as_str())))
}

fn host_parent_id(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::null());
    };
    let res = with_dom(|d| d.nodes.get(id).and_then(|n| n.parent).filter(|&p| p > 0));
    Ok(res.map(|p| JsValue::new(p as i32)).unwrap_or_else(JsValue::null))
}

fn host_root_ids(_: &JsValue, _args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    let joined = with_dom(|d| {
        format!(
            "{},{},{}",
            d.doc_el_id.map(|i| i as i32).unwrap_or(-1),
            d.head_id.map(|i| i as i32).unwrap_or(-1),
            d.body_id.map(|i| i as i32).unwrap_or(-1),
        )
    });
    Ok(JsValue::from(JsString::from(joined.as_str())))
}

fn host_doc_write(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let s = arg_str(args, 0, ctx);
    with_dom(|d| d.doc_writes.push(s));
    Ok(JsValue::undefined())
}

fn host_get_title(_: &JsValue, _args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    let title = with_dom(|d| d.title.clone());
    Ok(JsValue::from(JsString::from(title.as_str())))
}

fn host_set_title(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let new_title = arg_str(args, 0, ctx);
    with_dom(|d| {
        d.title = new_title.clone();
        if let Some(tid) = d.find_first_tag(0, "title") {
            d.set_text_content(tid, &new_title);
        }
    });
    Ok(JsValue::undefined())
}

fn host_env_info(_: &JsValue, _args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    let info = with_dom(|d| format!("{}\n{}\n{}", d.base_url, d.user_agent, if d.dark_mode { "1" } else { "0" }));
    Ok(JsValue::from(JsString::from(info.as_str())))
}

fn host_log(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let msg = arg_str(args, 0, ctx);
    with_dom(|d| {
        if d.console_logs.len() < 32 {
            d.console_logs.push(msg);
        }
    });
    Ok(JsValue::undefined())
}

fn host_queue_timer(_: &JsValue, args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(cb) = args.first() {
        if cb.is_callable() {
            with_dom(|d| {
                if d.pending_timers.len() < MAX_TIMERS_PER_PAGE {
                    d.pending_timers.push(cb.clone());
                }
            });
        }
    }
    Ok(JsValue::new(1))
}

fn host_add_dom_listener(_: &JsValue, args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(cb) = args.first() {
        if cb.is_callable() {
            with_dom(|d| {
                if d.dom_listeners.len() < MAX_TIMERS_PER_PAGE {
                    d.dom_listeners.push(cb.clone());
                }
            });
        }
    }
    Ok(JsValue::undefined())
}

fn register_host_bindings(ctx: &mut Context) -> JsResult<()> {
    let host_obj = ObjectInitializer::new(ctx)
        .function(NativeFunction::from_fn_ptr(host_get_by_id), js_string!("getById"), 1)
        .function(NativeFunction::from_fn_ptr(host_qs), js_string!("qs"), 2)
        .function(NativeFunction::from_fn_ptr(host_qsa_ids), js_string!("qsaIds"), 2)
        .function(NativeFunction::from_fn_ptr(host_create_el), js_string!("createEl"), 1)
        .function(NativeFunction::from_fn_ptr(host_create_text), js_string!("createText"), 1)
        .function(NativeFunction::from_fn_ptr(host_get_attr), js_string!("getAttr"), 2)
        .function(NativeFunction::from_fn_ptr(host_set_attr), js_string!("setAttr"), 3)
        .function(NativeFunction::from_fn_ptr(host_rm_attr), js_string!("rmAttr"), 2)
        .function(NativeFunction::from_fn_ptr(host_get_text), js_string!("getText"), 1)
        .function(NativeFunction::from_fn_ptr(host_set_text), js_string!("setText"), 2)
        .function(NativeFunction::from_fn_ptr(host_get_html), js_string!("getHtml"), 1)
        .function(NativeFunction::from_fn_ptr(host_set_html), js_string!("setHtml"), 2)
        .function(NativeFunction::from_fn_ptr(host_append), js_string!("append"), 2)
        .function(NativeFunction::from_fn_ptr(host_remove), js_string!("remove"), 1)
        .function(NativeFunction::from_fn_ptr(host_tag), js_string!("tag"), 1)
        .function(NativeFunction::from_fn_ptr(host_children_ids), js_string!("childrenIds"), 1)
        .function(NativeFunction::from_fn_ptr(host_parent_id), js_string!("parentId"), 1)
        .function(NativeFunction::from_fn_ptr(host_root_ids), js_string!("rootIds"), 0)
        .function(NativeFunction::from_fn_ptr(host_doc_write), js_string!("docWrite"), 1)
        .function(NativeFunction::from_fn_ptr(host_get_title), js_string!("getTitle"), 0)
        .function(NativeFunction::from_fn_ptr(host_set_title), js_string!("setTitle"), 1)
        .function(NativeFunction::from_fn_ptr(host_env_info), js_string!("envInfo"), 0)
        .function(NativeFunction::from_fn_ptr(host_log), js_string!("log"), 1)
        .function(NativeFunction::from_fn_ptr(host_queue_timer), js_string!("queueTimer"), 1)
        .function(NativeFunction::from_fn_ptr(host_add_dom_listener), js_string!("addDomListener"), 1)
        .build();

    let global = ctx.global_object();
    ObjectInitializer::with_object(global, ctx)
        .property(js_string!("__ws"), host_obj, boa_engine::property::Attribute::READONLY)
        .build();
    Ok(())
}

const JS_DOM_BOOTSTRAP: &str = r#"
(function(g) {
    var env = String(__ws.envInfo()).split('\n');
    var pageUrl = env[0] || 'https://localhost/';
    var uaStr = env[1] || 'Mozilla/5.0';
    var isDark = env[2] === '1';

    var nodeCache = {};
    function wrap(id) {
        if (id === null || id === undefined || id < 0) return null;
        if (nodeCache[id]) return nodeCache[id];
        var el = Object.create(WsElement.prototype);
        el.__id = id;
        nodeCache[id] = el;
        return el;
    }

    function parseIds(csv) {
        if (!csv) return [];
        var parts = String(csv).split(',');
        var out = [];
        for (var i = 0; i < parts.length; i++) {
            if (parts[i]) out.push(wrap(Number(parts[i])));
        }
        return out;
    }

    function WsElement() {}
    WsElement.prototype = {
        get id() { return __ws.getAttr(this.__id, 'id') || ''; },
        set id(v) { __ws.setAttr(this.__id, 'id', String(v)); },
        get className() { return __ws.getAttr(this.__id, 'class') || ''; },
        set className(v) { __ws.setAttr(this.__id, 'class', String(v)); },
        get tagName() { return __ws.tag(this.__id); },
        get nodeName() { return __ws.tag(this.__id); },
        get href() { return __ws.getAttr(this.__id, 'href') || ''; },
        set href(v) { __ws.setAttr(this.__id, 'href', String(v)); },
        get src() { return __ws.getAttr(this.__id, 'src') || ''; },
        set src(v) { __ws.setAttr(this.__id, 'src', String(v)); },
        get value() { return __ws.getAttr(this.__id, 'value') || ''; },
        set value(v) { __ws.setAttr(this.__id, 'value', String(v)); },
        get textContent() { return __ws.getText(this.__id); },
        set textContent(v) { __ws.setText(this.__id, v === null || v === undefined ? '' : String(v)); },
        get innerText() { return __ws.getText(this.__id); },
        set innerText(v) { __ws.setText(this.__id, v === null || v === undefined ? '' : String(v)); },
        get innerHTML() { return __ws.getHtml(this.__id); },
        set innerHTML(v) { __ws.setHtml(this.__id, v === null || v === undefined ? '' : String(v)); },
        get children() { return parseIds(__ws.childrenIds(this.__id)); },
        get parentNode() { return wrap(__ws.parentId(this.__id)); },
        get parentElement() { return wrap(__ws.parentId(this.__id)); },
        get firstElementChild() { var c = this.children; return c.length ? c[0] : null; },
        get style() {
            var self = this;
            return new Proxy({}, {
                get: function(_, prop) {
                    var s = __ws.getAttr(self.__id, 'style') || '';
                    var cssKey = String(prop).replace(/[A-Z]/g, function(m) { return '-' + m.toLowerCase(); });
                    var re = new RegExp('(?:^|;)\\s*' + cssKey + '\\s*:\\s*([^;]+)', 'i');
                    var m = s.match(re);
                    return m ? m[1].trim() : '';
                },
                set: function(_, prop, val) {
                    if (prop === 'cssText') {
                        __ws.setAttr(self.__id, 'style', String(val));
                        return true;
                    }
                    var cssKey = String(prop).replace(/[A-Z]/g, function(m) { return '-' + m.toLowerCase(); });
                    var s = __ws.getAttr(self.__id, 'style') || '';
                    var re = new RegExp('(?:^|;)\\s*' + cssKey + '\\s*:[^;]*', 'ig');
                    var cleaned = s.replace(re, '').replace(/^;+|;+$/g, '');
                    var entry = cssKey + ':' + String(val);
                    __ws.setAttr(self.__id, 'style', cleaned ? cleaned + ';' + entry : entry);
                    return true;
                }
            });
        },
        get classList() {
            var self = this;
            function list() { return (self.className || '').split(/\s+/).filter(Boolean); }
            return {
                contains: function(c) { return list().indexOf(c) !== -1; },
                add: function() {
                    var cur = list();
                    for (var i = 0; i < arguments.length; i++) {
                        if (cur.indexOf(arguments[i]) === -1) cur.push(arguments[i]);
                    }
                    self.className = cur.join(' ');
                },
                remove: function() {
                    var cur = list();
                    for (var i = 0; i < arguments.length; i++) {
                        cur = cur.filter(function(x) { return x !== arguments[i]; });
                    }
                    self.className = cur.join(' ');
                },
                toggle: function(c) {
                    if (this.contains(c)) { this.remove(c); return false; }
                    this.add(c); return true;
                }
            };
        },
        getAttribute: function(k) { return __ws.getAttr(this.__id, String(k)); },
        setAttribute: function(k, v) { __ws.setAttr(this.__id, String(k), String(v)); },
        removeAttribute: function(k) { __ws.rmAttr(this.__id, String(k)); },
        hasAttribute: function(k) { return __ws.getAttr(this.__id, String(k)) !== null; },
        appendChild: function(child) { if (child && child.__id >= 0) __ws.append(this.__id, child.__id); return child; },
        append: function() {
            for (var i = 0; i < arguments.length; i++) {
                var a = arguments[i];
                if (typeof a === 'string') { __ws.append(this.__id, __ws.createText(a)); }
                else if (a && a.__id >= 0) { __ws.append(this.__id, a.__id); }
            }
        },
        remove: function() { __ws.remove(this.__id); },
        querySelector: function(sel) { return wrap(__ws.qs(this.__id, String(sel))); },
        querySelectorAll: function(sel) { return parseIds(__ws.qsaIds(this.__id, String(sel))); },
        getElementsByTagName: function(tag) { return parseIds(__ws.qsaIds(this.__id, String(tag))); },
        getElementsByClassName: function(cls) { return parseIds(__ws.qsaIds(this.__id, '.' + String(cls).trim().split(/\s+/).join('.'))); },
        addEventListener: function() {},
        removeEventListener: function() {},
        focus: function() {},
        blur: function() {},
        click: function() {}
    };

    var roots = String(__ws.rootIds()).split(',');
    var docEl = wrap(Number(roots[0]));
    var headEl = wrap(Number(roots[1]));
    var bodyEl = wrap(Number(roots[2]));

    var doc = {
        readyState: 'interactive',
        documentElement: docEl,
        head: headEl,
        body: bodyEl,
        get title() { return __ws.getTitle(); },
        set title(v) { __ws.setTitle(String(v)); },
        getElementById: function(id) { return wrap(__ws.getById(String(id))); },
        querySelector: function(sel) { return wrap(__ws.qs(0, String(sel))); },
        querySelectorAll: function(sel) { return parseIds(__ws.qsaIds(0, String(sel))); },
        getElementsByTagName: function(tag) { return parseIds(__ws.qsaIds(0, String(tag))); },
        getElementsByClassName: function(cls) { return parseIds(__ws.qsaIds(0, '.' + String(cls).trim())); },
        createElement: function(tag) { return wrap(__ws.createEl(String(tag))); },
        createTextNode: function(txt) { return wrap(__ws.createText(String(txt))); },
        createDocumentFragment: function() { return wrap(__ws.createEl('div')); },
        write: function() {
            var s = '';
            for (var i = 0; i < arguments.length; i++) s += String(arguments[i]);
            __ws.docWrite(s);
        },
        writeln: function() {
            var s = '';
            for (var i = 0; i < arguments.length; i++) s += String(arguments[i]);
            __ws.docWrite(s + '\n');
        },
        addEventListener: function(ev, fn) {
            if (ev === 'DOMContentLoaded' && typeof fn === 'function') __ws.addDomListener(fn);
        },
        removeEventListener: function() {}
    };

    var loc = { href: pageUrl, toString: function() { return pageUrl; } };
    try {
        var m = pageUrl.match(/^(https?:)\/\/([^\/?#]+)([^?#]*)(\?[^#]*)?(#.*)?$/i);
        if (m) {
            loc.protocol = m[1];
            loc.host = m[2];
            loc.hostname = m[2].split(':')[0];
            loc.origin = m[1] + '//' + m[2];
            loc.pathname = m[3] || '/';
            loc.search = m[4] || '';
            loc.hash = m[5] || '';
        }
    } catch (_) {}

    function mkStorage() {
        var data = {};
        return {
            getItem: function(k) { return Object.prototype.hasOwnProperty.call(data, k) ? data[k] : null; },
            setItem: function(k, v) { data[String(k)] = String(v); },
            removeItem: function(k) { delete data[String(k)]; },
            clear: function() { data = {}; }
        };
    }

    g.window = g;
    g.self = g;
    g.top = g;
    g.parent = g;
    g.document = doc;
    g.location = loc;
    g.navigator = { userAgent: uaStr, language: 'vi-VN', languages: ['vi-VN', 'en-US'], platform: 'Win32' };
    g.localStorage = mkStorage();
    g.sessionStorage = mkStorage();
    g.console = {
        log: function() { __ws.log(Array.prototype.join.call(arguments, ' ')); },
        warn: function() { __ws.log(Array.prototype.join.call(arguments, ' ')); },
        error: function() { __ws.log(Array.prototype.join.call(arguments, ' ')); },
        info: function() {}
    };
    g.setTimeout = function(fn) { if (typeof fn === 'function') return __ws.queueTimer(fn); return 0; };
    g.clearTimeout = function() {};
    g.setInterval = function() { return 0; };
    g.clearInterval = function() {};
    g.queueMicrotask = function(fn) { if (typeof fn === 'function') __ws.queueTimer(fn); };
    g.requestAnimationFrame = function(fn) { if (typeof fn === 'function') return __ws.queueTimer(fn); return 0; };
    g.cancelAnimationFrame = function() {};
    g.matchMedia = function(q) {
        var matches = /prefers-color-scheme\s*:\s*dark/i.test(String(q)) ? isDark : false;
        return { matches: matches, media: String(q), addListener: function(){}, removeListener: function(){}, addEventListener: function(){} };
    };
    g.addEventListener = function(ev, fn) {
        if ((ev === 'load' || ev === 'DOMContentLoaded') && typeof fn === 'function') __ws.addDomListener(fn);
    };
    g.removeEventListener = function() {};
    g.alert = function() {};
    g.confirm = function() { return false; };
    g.prompt = function() { return null; };
    g.Event = function(type) { this.type = type; };
    g.CustomEvent = function(type, opts) { this.type = type; this.detail = opts ? opts.detail : undefined; };
    g.fetch = function() { return Promise.reject(new Error('fetch disabled in offline layout pass')); };
})(globalThis);
"#;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_dom_mutation_and_bootstrap() {
        let html = r#"
            <html><head><title>Init</title></head>
            <body>
                <div id="app" class="hidden"><p>Old</p></div>
                <script>
                    document.title = "JS Title";
                    var app = document.getElementById("app");
                    app.classList.remove("hidden");
                    app.innerHTML = "<p class='msg'>Hello from Boa!</p>";
                    var extra = document.createElement("span");
                    extra.textContent = " Added";
                    extra.style.color = "#ff0000";
                    app.appendChild(extra);
                    document.addEventListener("DOMContentLoaded", function() {
                        var p = document.querySelector(".msg");
                        if (p) p.textContent = "Hydrated";
                    });
                </script>
            </body></html>
        "#;
        let res = execute_page_scripts(html, "https://example.com/a", "", "TestUA", false);
        assert_eq!(res.scripts_executed, 1);
        assert_eq!(res.errors, 0);
        assert!(res.html.contains("JS Title"));
        assert!(res.html.contains("Hydrated"));
        assert!(res.html.contains("Added"));
        assert!(res.html.contains("color:#ff0000"));
        assert!(!res.html.contains("class=\"hidden\""));
    }

    #[test]
    fn test_infinite_loop_protection() {
        let html = r#"
            <html><body>
                <p id="ok">Safe</p>
                <script>while (true) {}</script>
                <script>document.getElementById("ok").textContent = "Survived";</script>
            </body></html>
        "#;
        let res = execute_page_scripts(html, "https://example.com", "", "TestUA", false);
        assert_eq!(res.scripts_executed, 2);
        assert_eq!(res.errors, 1);
        assert!(res.html.contains("Survived"));
    }

    #[test]
    fn test_extract_script_urls() {
        let html = r#"
            <html><head>
                <script src="/app.js"></script>
                <script type="application/ld+json" src="/schema.json"></script>
                <script src="https://cdn.example.com/lib.js"></script>
            </head></html>
        "#;
        let urls = extract_script_urls(html, "https://example.com/page");
        assert_eq!(
            urls,
            vec![
                "https://example.com/app.js",
                "https://cdn.example.com/lib.js"
            ]
        );
    }
}
