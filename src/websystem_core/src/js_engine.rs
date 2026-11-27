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
    Fragment,
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
    cookies: Vec<(String, String)>,
    pending_timers: Vec<(u32, JsValue)>,
    cancelled_timers: Vec<u32>,
    next_timer_id: u32,
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
            cookies: Vec::new(),
            pending_timers: Vec::new(),
            cancelled_timers: Vec::new(),
            next_timer_id: 1,
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
        if matches!(self.nodes[child_id].kind, DomKind::Fragment) {
            let frag_children = std::mem::take(&mut self.nodes[child_id].children);
            for fc in frag_children {
                self.append_child(parent_id, fc);
            }
            return;
        }
        self.detach_node(child_id);
        self.nodes[child_id].parent = Some(parent_id);
        self.nodes[parent_id].children.push(child_id);
        self.mutated = true;
    }

    fn insert_before(&mut self, parent_id: usize, new_id: usize, ref_id: Option<usize>) {
        if parent_id >= self.nodes.len() || new_id == 0 || new_id >= self.nodes.len() {
            return;
        }
        if parent_id == new_id {
            return;
        }
        if matches!(self.nodes[new_id].kind, DomKind::Fragment) {
            let frag_children = std::mem::take(&mut self.nodes[new_id].children);
            for fc in frag_children {
                self.insert_before(parent_id, fc, ref_id);
            }
            return;
        }
        self.detach_node(new_id);
        self.nodes[new_id].parent = Some(parent_id);
        let pos = ref_id.and_then(|rid| self.nodes[parent_id].children.iter().position(|&c| c == rid));
        if let Some(idx) = pos {
            self.nodes[parent_id].children.insert(idx, new_id);
        } else {
            self.nodes[parent_id].children.push(new_id);
        }
        self.mutated = true;
    }

    fn replace_child(&mut self, parent_id: usize, new_id: usize, old_id: usize) {
        if parent_id >= self.nodes.len() || old_id == 0 || old_id >= self.nodes.len() {
            return;
        }
        if !self.nodes[parent_id].children.contains(&old_id) {
            return;
        }
        self.insert_before(parent_id, new_id, Some(old_id));
        self.detach_node(old_id);
        self.mutated = true;
    }

    fn clone_node(&mut self, id: usize, deep: bool) -> Option<usize> {
        if id == 0 || id >= self.nodes.len() || self.nodes.len() >= MAX_DOM_NODES {
            return None;
        }
        let kind = self.nodes[id].kind.clone();
        let children = if deep {
            self.nodes[id].children.clone()
        } else {
            Vec::new()
        };
        let cloned_id = self.alloc_node(kind, None);
        if deep {
            for ch in children {
                if let Some(cloned_ch) = self.clone_node(ch, true) {
                    self.nodes[cloned_ch].parent = Some(cloned_id);
                    self.nodes[cloned_id].children.push(cloned_ch);
                }
            }
        }
        self.mutated = true;
        Some(cloned_id)
    }

    fn element_sibling(&self, id: usize, next: bool) -> Option<usize> {
        let parent_id = self.nodes.get(id)?.parent?;
        let siblings = &self.nodes.get(parent_id)?.children;
        let pos = siblings.iter().position(|&c| c == id)?;
        if next {
            for &sid in &siblings[pos + 1..] {
                if matches!(self.nodes.get(sid).map(|n| &n.kind), Some(DomKind::Element { .. })) {
                    return Some(sid);
                }
            }
        } else {
            for &sid in siblings[..pos].iter().rev() {
                if matches!(self.nodes.get(sid).map(|n| &n.kind), Some(DomKind::Element { .. })) {
                    return Some(sid);
                }
            }
        }
        None
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
        let normalized = selector.replace('>', " > ");
        let parts: Vec<&str> = normalized.split_whitespace().collect();
        if parts.is_empty() {
            return false;
        }
        let Some(&last) = parts.last() else {
            return false;
        };
        if last == ">" || !self.matches_simple_selector(id, last) {
            return false;
        }
        let mut cur_id = id;
        let mut direct_child = false;
        for &tok in parts[..parts.len() - 1].iter().rev() {
            if tok == ">" {
                direct_child = true;
                continue;
            }
            if direct_child {
                let Some(pid) = self.nodes.get(cur_id).and_then(|n| n.parent) else {
                    return false;
                };
                if !self.matches_simple_selector(pid, tok) {
                    return false;
                }
                cur_id = pid;
                direct_child = false;
            } else {
                let mut matched = false;
                let mut p = self.nodes.get(cur_id).and_then(|n| n.parent);
                while let Some(pid) = p {
                    if self.matches_simple_selector(pid, tok) {
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

    fn serialize_outer_html(&self, id: usize) -> String {
        let mut out = String::new();
        self.serialize_node(id, &mut out, false);
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
            DomKind::Document | DomKind::Fragment => {
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
        let timers = with_dom(|d| std::mem::take(&mut d.pending_timers));
        if timers.is_empty() {
            break;
        }
        for (tid, cb) in timers {
            let is_cancelled = with_dom(|d| d.cancelled_timers.contains(&tid));
            if is_cancelled {
                continue;
            }
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

fn host_create_frag(_: &JsValue, _args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    let res = with_dom(|d| {
        if d.nodes.len() >= MAX_DOM_NODES {
            return None;
        }
        Some(d.alloc_node(DomKind::Fragment, None))
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

fn host_outer_html(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::from(js_string!("")));
    };
    let out = with_dom(|d| d.serialize_outer_html(id));
    Ok(JsValue::from(JsString::from(out.as_str())))
}

fn host_append(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let (Some(pid), Some(cid)) = (arg_id(args, 0, ctx), arg_id(args, 1, ctx)) {
        with_dom(|d| d.append_child(pid, cid));
    }
    Ok(JsValue::undefined())
}

fn host_insert_before(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let (Some(pid), Some(nid)) = (arg_id(args, 0, ctx), arg_id(args, 1, ctx)) {
        let rid = arg_id(args, 2, ctx);
        with_dom(|d| d.insert_before(pid, nid, rid));
    }
    Ok(JsValue::undefined())
}

fn host_replace_child(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let (Some(pid), Some(nid), Some(oid)) = (
        arg_id(args, 0, ctx),
        arg_id(args, 1, ctx),
        arg_id(args, 2, ctx),
    ) {
        with_dom(|d| d.replace_child(pid, nid, oid));
    }
    Ok(JsValue::undefined())
}

fn host_clone_node(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::null());
    };
    let deep = args.get(1).map(|v| v.to_boolean()).unwrap_or(false);
    let res = with_dom(|d| d.clone_node(id, deep));
    Ok(res.map(|nid| JsValue::new(nid as i32)).unwrap_or_else(JsValue::null))
}

fn host_sibling_id(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::null());
    };
    let next = args.get(1).map(|v| v.to_boolean()).unwrap_or(true);
    let res = with_dom(|d| d.element_sibling(id, next));
    Ok(res.map(|sid| JsValue::new(sid as i32)).unwrap_or_else(JsValue::null))
}

fn host_child_nodes_ids(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let Some(id) = arg_id(args, 0, ctx) else {
        return Ok(JsValue::from(js_string!("")));
    };
    let joined = with_dom(|d| {
        d.nodes
            .get(id)
            .map(|n| {
                n.children
                    .iter()
                    .map(|cid| cid.to_string())
                    .collect::<Vec<_>>()
                    .join(",")
            })
            .unwrap_or_default()
    });
    Ok(JsValue::from(JsString::from(joined.as_str())))
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
    let mut timer_id = 0i32;
    if let Some(cb) = args.first() {
        if cb.is_callable() {
            with_dom(|d| {
                if d.pending_timers.len() < MAX_TIMERS_PER_PAGE {
                    let id = d.next_timer_id;
                    d.next_timer_id = d.next_timer_id.saturating_add(1);
                    d.pending_timers.push((id, cb.clone()));
                    timer_id = id as i32;
                }
            });
        }
    }
    Ok(JsValue::new(timer_id))
}

fn host_cancel_timer(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    if let Some(id) = arg_id(args, 0, ctx) {
        with_dom(|d| {
            if id > 0 && d.cancelled_timers.len() < MAX_TIMERS_PER_PAGE * 2 {
                d.cancelled_timers.push(id as u32);
            }
        });
    }
    Ok(JsValue::undefined())
}

fn host_get_cookie(_: &JsValue, _args: &[JsValue], _ctx: &mut Context) -> JsResult<JsValue> {
    let joined = with_dom(|d| {
        d.cookies
            .iter()
            .map(|(k, v)| format!("{}={}", k, v))
            .collect::<Vec<_>>()
            .join("; ")
    });
    Ok(JsValue::from(JsString::from(joined.as_str())))
}

fn host_set_cookie(_: &JsValue, args: &[JsValue], ctx: &mut Context) -> JsResult<JsValue> {
    let raw = arg_str(args, 0, ctx);
    let pair = raw.split(';').next().unwrap_or("").trim();
    if let Some((k_raw, v_raw)) = pair.split_once('=') {
        let k = k_raw.trim().to_string();
        let v = v_raw.trim().to_string();
        if !k.is_empty() {
            with_dom(|d| {
                if let Some((_, existing)) = d.cookies.iter_mut().find(|(ek, _)| ek == &k) {
                    *existing = v;
                } else if d.cookies.len() < 64 {
                    d.cookies.push((k, v));
                }
            });
        }
    }
    Ok(JsValue::undefined())
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
        .function(NativeFunction::from_fn_ptr(host_create_frag), js_string!("createFrag"), 0)
        .function(NativeFunction::from_fn_ptr(host_get_attr), js_string!("getAttr"), 2)
        .function(NativeFunction::from_fn_ptr(host_set_attr), js_string!("setAttr"), 3)
        .function(NativeFunction::from_fn_ptr(host_rm_attr), js_string!("rmAttr"), 2)
        .function(NativeFunction::from_fn_ptr(host_get_text), js_string!("getText"), 1)
        .function(NativeFunction::from_fn_ptr(host_set_text), js_string!("setText"), 2)
        .function(NativeFunction::from_fn_ptr(host_get_html), js_string!("getHtml"), 1)
        .function(NativeFunction::from_fn_ptr(host_set_html), js_string!("setHtml"), 2)
        .function(NativeFunction::from_fn_ptr(host_outer_html), js_string!("outerHtml"), 1)
        .function(NativeFunction::from_fn_ptr(host_append), js_string!("append"), 2)
        .function(NativeFunction::from_fn_ptr(host_insert_before), js_string!("insertBefore"), 3)
        .function(NativeFunction::from_fn_ptr(host_replace_child), js_string!("replaceChild"), 3)
        .function(NativeFunction::from_fn_ptr(host_clone_node), js_string!("cloneNode"), 2)
        .function(NativeFunction::from_fn_ptr(host_remove), js_string!("remove"), 1)
        .function(NativeFunction::from_fn_ptr(host_tag), js_string!("tag"), 1)
        .function(NativeFunction::from_fn_ptr(host_children_ids), js_string!("childrenIds"), 1)
        .function(NativeFunction::from_fn_ptr(host_child_nodes_ids), js_string!("childNodesIds"), 1)
        .function(NativeFunction::from_fn_ptr(host_sibling_id), js_string!("siblingId"), 2)
        .function(NativeFunction::from_fn_ptr(host_parent_id), js_string!("parentId"), 1)
        .function(NativeFunction::from_fn_ptr(host_root_ids), js_string!("rootIds"), 0)
        .function(NativeFunction::from_fn_ptr(host_doc_write), js_string!("docWrite"), 1)
        .function(NativeFunction::from_fn_ptr(host_get_title), js_string!("getTitle"), 0)
        .function(NativeFunction::from_fn_ptr(host_set_title), js_string!("setTitle"), 1)
        .function(NativeFunction::from_fn_ptr(host_get_cookie), js_string!("getCookie"), 0)
        .function(NativeFunction::from_fn_ptr(host_set_cookie), js_string!("setCookie"), 1)
        .function(NativeFunction::from_fn_ptr(host_env_info), js_string!("envInfo"), 0)
        .function(NativeFunction::from_fn_ptr(host_log), js_string!("log"), 1)
        .function(NativeFunction::from_fn_ptr(host_queue_timer), js_string!("queueTimer"), 1)
        .function(NativeFunction::from_fn_ptr(host_cancel_timer), js_string!("cancelTimer"), 1)
        .function(NativeFunction::from_fn_ptr(host_add_dom_listener), js_string!("addDomListener"), 1)
        .build();

    ctx.register_global_property(
        js_string!("__ws"),
        host_obj,
        boa_engine::property::Attribute::READONLY,
    )?;
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

    function maybeRunScript(node) {
        if (!node || node.__wsRan || node.tagName !== 'SCRIPT' || node.src) return;
        var ty = (node.getAttribute('type') || '').trim().toLowerCase();
        if (ty && ty !== 'text/javascript' && ty !== 'application/javascript' && ty !== 'module') return;
        node.__wsRan = true;
        var code = node.textContent;
        if (code && code.trim()) {
            try { (0, eval)(code); } catch (e) { __ws.log(String(e)); }
        }
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
        get outerHTML() { return __ws.outerHtml(this.__id); },
        get children() { return parseIds(__ws.childrenIds(this.__id)); },
        get childNodes() { return parseIds(__ws.childNodesIds(this.__id)); },
        get parentNode() { return wrap(__ws.parentId(this.__id)); },
        get parentElement() { return wrap(__ws.parentId(this.__id)); },
        get firstElementChild() { var c = this.children; return c.length ? c[0] : null; },
        get lastElementChild() { var c = this.children; return c.length ? c[c.length - 1] : null; },
        get previousElementSibling() { return wrap(__ws.siblingId(this.__id, false)); },
        get nextElementSibling() { return wrap(__ws.siblingId(this.__id, true)); },
        get dataset() {
            var self = this;
            return new Proxy({}, {
                get: function(_, prop) {
                    var attr = 'data-' + String(prop).replace(/[A-Z]/g, function(m) { return '-' + m.toLowerCase(); });
                    var v = __ws.getAttr(self.__id, attr);
                    return v === null ? undefined : v;
                },
                set: function(_, prop, val) {
                    var attr = 'data-' + String(prop).replace(/[A-Z]/g, function(m) { return '-' + m.toLowerCase(); });
                    __ws.setAttr(self.__id, attr, String(val));
                    return true;
                },
                deleteProperty: function(_, prop) {
                    var attr = 'data-' + String(prop).replace(/[A-Z]/g, function(m) { return '-' + m.toLowerCase(); });
                    __ws.rmAttr(self.__id, attr);
                    return true;
                }
            });
        },
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
        appendChild: function(child) {
            if (child && child.__id >= 0) {
                __ws.append(this.__id, child.__id);
                maybeRunScript(child);
            }
            return child;
        },
        insertBefore: function(newNode, refNode) {
            if (newNode && newNode.__id >= 0) {
                __ws.insertBefore(this.__id, newNode.__id, refNode && refNode.__id >= 0 ? refNode.__id : -1);
                maybeRunScript(newNode);
            }
            return newNode;
        },
        replaceChild: function(newNode, oldNode) {
            if (newNode && newNode.__id >= 0 && oldNode && oldNode.__id >= 0) {
                __ws.replaceChild(this.__id, newNode.__id, oldNode.__id);
                maybeRunScript(newNode);
            }
            return oldNode;
        },
        cloneNode: function(deep) { return wrap(__ws.cloneNode(this.__id, !!deep)); },
        append: function() {
            for (var i = 0; i < arguments.length; i++) {
                var a = arguments[i];
                if (typeof a === 'string') { __ws.append(this.__id, __ws.createText(a)); }
                else if (a && a.__id >= 0) {
                    __ws.append(this.__id, a.__id);
                    maybeRunScript(a);
                }
            }
        },
        remove: function() { __ws.remove(this.__id); },
        querySelector: function(sel) { return wrap(__ws.qs(this.__id, String(sel))); },
        querySelectorAll: function(sel) { return parseIds(__ws.qsaIds(this.__id, String(sel))); },
        getElementsByTagName: function(tag) { return parseIds(__ws.qsaIds(this.__id, String(tag))); },
        getElementsByClassName: function(cls) { return parseIds(__ws.qsaIds(this.__id, '.' + String(cls).trim().split(/\s+/).join('.'))); },
        matches: function(sel) {
            var hits = parseIds(__ws.qsaIds(0, String(sel)));
            for (var i = 0; i < hits.length; i++) {
                if (hits[i] && hits[i].__id === this.__id) return true;
            }
            return false;
        },
        closest: function(sel) {
            var cur = this;
            while (cur && cur.__id > 0) {
                if (cur.matches(sel)) return cur;
                cur = cur.parentElement;
            }
            return null;
        },
        addEventListener: function(ev, fn) {
            if (typeof fn !== 'function') return;
            if (!this.__listeners) this.__listeners = {};
            if (!this.__listeners[ev]) this.__listeners[ev] = [];
            if (this.__listeners[ev].indexOf(fn) === -1) this.__listeners[ev].push(fn);
        },
        removeEventListener: function(ev, fn) {
            if (!this.__listeners || !this.__listeners[ev]) return;
            this.__listeners[ev] = this.__listeners[ev].filter(function(f) { return f !== fn; });
        },
        dispatchEvent: function(ev) {
            var evType = typeof ev === 'string' ? ev : (ev && ev.type ? String(ev.type) : '');
            if (!evType || !this.__listeners || !this.__listeners[evType]) return true;
            var evt = (typeof ev === 'object' && ev) ? ev : { type: evType };
            if (!evt.target) evt.target = this;
            evt.currentTarget = this;
            if (!evt.preventDefault) evt.preventDefault = function() {};
            if (!evt.stopPropagation) evt.stopPropagation = function() {};
            var list = this.__listeners[evType].slice();
            for (var i = 0; i < list.length; i++) {
                list[i].call(this, evt);
            }
            return true;
        },
        focus: function() {},
        blur: function() {},
        click: function() { this.dispatchEvent(new g.Event('click')); }
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
        get cookie() { return __ws.getCookie(); },
        set cookie(v) { __ws.setCookie(String(v)); },
        getElementById: function(id) { return wrap(__ws.getById(String(id))); },
        querySelector: function(sel) { return wrap(__ws.qs(0, String(sel))); },
        querySelectorAll: function(sel) { return parseIds(__ws.qsaIds(0, String(sel))); },
        getElementsByTagName: function(tag) { return parseIds(__ws.qsaIds(0, String(tag))); },
        getElementsByClassName: function(cls) { return parseIds(__ws.qsaIds(0, '.' + String(cls).trim())); },
        createElement: function(tag) { return wrap(__ws.createEl(String(tag))); },
        createTextNode: function(txt) { return wrap(__ws.createText(String(txt))); },
        createDocumentFragment: function() { return wrap(__ws.createFrag()); },
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
    function WsURLSearchParams(init) {
        this._entries = [];
        if (typeof init === 'string') {
            var s = init.charAt(0) === '?' ? init.slice(1) : init;
            if (s) {
                var parts = s.split('&');
                for (var i = 0; i < parts.length; i++) {
                    if (!parts[i]) continue;
                    var eq = parts[i].indexOf('=');
                    var k = eq >= 0 ? parts[i].slice(0, eq) : parts[i];
                    var v = eq >= 0 ? parts[i].slice(eq + 1) : '';
                    try { k = decodeURIComponent(k.replace(/\+/g, ' ')); } catch (_) {}
                    try { v = decodeURIComponent(v.replace(/\+/g, ' ')); } catch (_) {}
                    this._entries.push([k, v]);
                }
            }
        }
    }
    WsURLSearchParams.prototype = {
        append: function(k, v) { this._entries.push([String(k), String(v)]); },
        get: function(k) {
            var ks = String(k);
            for (var i = 0; i < this._entries.length; i++) {
                if (this._entries[i][0] === ks) return this._entries[i][1];
            }
            return null;
        },
        getAll: function(k) {
            var ks = String(k), out = [];
            for (var i = 0; i < this._entries.length; i++) {
                if (this._entries[i][0] === ks) out.push(this._entries[i][1]);
            }
            return out;
        },
        has: function(k) { return this.get(k) !== null; },
        set: function(k, v) {
            var ks = String(k), vs = String(v), replaced = false, next = [];
            for (var i = 0; i < this._entries.length; i++) {
                if (this._entries[i][0] === ks) {
                    if (!replaced) { next.push([ks, vs]); replaced = true; }
                } else {
                    next.push(this._entries[i]);
                }
            }
            if (!replaced) next.push([ks, vs]);
            this._entries = next;
        },
        'delete': function(k) {
            var ks = String(k);
            this._entries = this._entries.filter(function(e) { return e[0] !== ks; });
        },
        toString: function() {
            return this._entries.map(function(e) {
                return encodeURIComponent(e[0]) + '=' + encodeURIComponent(e[1]);
            }).join('&');
        }
    };

    function WsURL(input, base) {
        var raw = String(input || '');
        if (!/^[a-z][a-z0-9+.-]*:/i.test(raw) && base) {
            var b = String(base);
            var bm = b.match(/^(https?:)\/\/([^\/?#]+)([^?#]*)/i);
            if (bm) {
                if (raw.indexOf('//') === 0) raw = bm[1] + raw;
                else if (raw.charAt(0) === '/') raw = bm[1] + '//' + bm[2] + raw;
                else {
                    var dir = (bm[3] || '/').replace(/[^\/]*$/, '');
                    raw = bm[1] + '//' + bm[2] + dir + raw;
                }
            }
        }
        var m = raw.match(/^(https?:)\/\/([^\/?#]+)([^?#]*)(\?[^#]*)?(#.*)?$/i);
        if (!m) throw new Error('Invalid URL: ' + raw);
        this.protocol = m[1];
        this.host = m[2];
        var hp = m[2].split(':');
        this.hostname = hp[0];
        this.port = hp[1] || '';
        this.origin = m[1] + '//' + m[2];
        this.pathname = m[3] || '/';
        this.search = m[4] || '';
        this.hash = m[5] || '';
        this.searchParams = new WsURLSearchParams(this.search);
        this.href = this.origin + this.pathname + this.search + this.hash;
        this.toString = function() {
            var qs = this.searchParams.toString();
            var s = qs ? ('?' + qs) : '';
            return this.origin + this.pathname + s + this.hash;
        };
    }

    g.URLSearchParams = WsURLSearchParams;
    g.URL = WsURL;
    g.setTimeout = function(fn) { if (typeof fn === 'function') return __ws.queueTimer(fn); return 0; };
    g.clearTimeout = function(id) { if (id) __ws.cancelTimer(Number(id)); };
    g.setInterval = function(fn) { if (typeof fn === 'function') return __ws.queueTimer(fn); return 0; };
    g.clearInterval = function(id) { if (id) __ws.cancelTimer(Number(id)); };
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
        let html = r##"
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
        "##;
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

    #[test]
    fn test_fragment_events_timers_url_and_dynamic_script() {
        let html = r##"
            <html><body>
                <div id="wrap"><span id="ref">End</span></div>
                <button id="btn" data-user-name="alice">Click</button>
                <p id="out"></p>
                <script>
                    var frag = document.createDocumentFragment();
                    var li = document.createElement("p");
                    li.textContent = "FragItem";
                    frag.appendChild(li);
                    var wrap = document.getElementById("wrap");
                    var refEl = document.getElementById("ref");
                    wrap.insertBefore(frag, refEl);

                    var btn = document.getElementById("btn");
                    btn.addEventListener("click", function() {
                        document.cookie = "theme=dark; path=/";
                        var u = new URL("/search?q=lumia", location.href);
                        var p = document.querySelector("#wrap > p");
                        document.getElementById("out").textContent =
                            btn.dataset.userName + "|" + u.searchParams.get("q") + "|" + document.cookie + "|" + (p ? p.textContent : "");
                    });
                    btn.click();

                    var cancelledId = setTimeout(function() {
                        document.getElementById("out").textContent = "SHOULD_NOT_RUN";
                    }, 10);
                    clearTimeout(cancelledId);

                    var dyn = document.createElement("script");
                    dyn.textContent = "document.getElementById('ref').textContent = 'DynOK';";
                    document.body.appendChild(dyn);
                </script>
            </body></html>
        "##;
        let res = execute_page_scripts(html, "https://example.com/page", "", "TestUA", false);
        assert_eq!(res.errors, 0);
        assert!(res.html.contains("FragItem"));
        assert!(res.html.contains("alice|lumia|theme=dark|FragItem"));
        assert!(res.html.contains("DynOK"));
        assert!(!res.html.contains("SHOULD_NOT_RUN"));
    }
}
