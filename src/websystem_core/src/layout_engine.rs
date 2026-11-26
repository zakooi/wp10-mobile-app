use scraper::{ElementRef, Html, Node, Selector};
use std::collections::HashMap;
use taffy::prelude::*;

const RENDER_MAGIC: u32 = 0x5753_5231; // "WSR1"
const MAX_RENDER_NODES: usize = 3500;

const FLAG_BOLD: u8 = 1;
const FLAG_ITALIC: u8 = 2;
const FLAG_UNDERLINE: u8 = 4;

const KIND_BOX: u8 = 0;
const KIND_TEXT: u8 = 1;
const KIND_LINK: u8 = 2;
const KIND_DIVIDER: u8 = 3;

#[derive(Clone, Copy)]
struct ThemePalette {
    page_bg: u32,
    surface_bg: u32,
    text_fg: u32,
    muted_fg: u32,
    link_fg: u32,
    border_fg: u32,
    code_bg: u32,
    button_bg: u32,
}

impl ThemePalette {
    fn for_mode(dark_mode: bool) -> Self {
        if dark_mode {
            Self {
                page_bg: 0xFF12_1212,
                surface_bg: 0xFF1E_1E1E,
                text_fg: 0xFFE6_E6E6,
                muted_fg: 0xFF9E_9E9E,
                link_fg: 0xFF8A_B4F8,
                border_fg: 0xFF38_3838,
                code_bg: 0xFF22_252A,
                button_bg: 0xFF2B_313A,
            }
        } else {
            Self {
                page_bg: 0xFFFA_FAFA,
                surface_bg: 0xFFFF_FFFF,
                text_fg: 0xFF1F_2328,
                muted_fg: 0xFF59_636E,
                link_fg: 0xFF09_69DA,
                border_fg: 0xFFD0_D7DE,
                code_bg: 0xFFF0_F3F6,
                button_bg: 0xFFE8_EEF5,
            }
        }
    }
}

#[derive(Clone, Default)]
struct CssProps {
    display_none: bool,
    display_flex: Option<FlexDirection>,
    color: Option<u32>,
    bg_color: Option<u32>,
    border_color: Option<u32>,
    font_size: Option<f32>,
    bold: Option<bool>,
    italic: Option<bool>,
    margin_top: Option<f32>,
    margin_bottom: Option<f32>,
    margin_left: Option<f32>,
    margin_right: Option<f32>,
    padding_v: Option<f32>,
    padding_h: Option<f32>,
    width_px: Option<f32>,
    height_px: Option<f32>,
}

impl CssProps {
    fn merge_from(&mut self, other: &CssProps) {
        if other.display_none {
            self.display_none = true;
        }
        if other.display_flex.is_some() {
            self.display_flex = other.display_flex;
        }
        if other.color.is_some() {
            self.color = other.color;
        }
        if other.bg_color.is_some() {
            self.bg_color = other.bg_color;
        }
        if other.border_color.is_some() {
            self.border_color = other.border_color;
        }
        if other.font_size.is_some() {
            self.font_size = other.font_size;
        }
        if other.bold.is_some() {
            self.bold = other.bold;
        }
        if other.italic.is_some() {
            self.italic = other.italic;
        }
        if other.margin_top.is_some() {
            self.margin_top = other.margin_top;
        }
        if other.margin_bottom.is_some() {
            self.margin_bottom = other.margin_bottom;
        }
        if other.margin_left.is_some() {
            self.margin_left = other.margin_left;
        }
        if other.margin_right.is_some() {
            self.margin_right = other.margin_right;
        }
        if other.padding_v.is_some() {
            self.padding_v = other.padding_v;
        }
        if other.padding_h.is_some() {
            self.padding_h = other.padding_h;
        }
        if other.width_px.is_some() {
            self.width_px = other.width_px;
        }
        if other.height_px.is_some() {
            self.height_px = other.height_px;
        }
    }
}

struct StyleSheet {
    by_tag: HashMap<String, CssProps>,
    by_class: HashMap<String, CssProps>,
    by_id: HashMap<String, CssProps>,
}

impl StyleSheet {
    fn parse(document: &Html) -> Self {
        let mut sheet = Self {
            by_tag: HashMap::new(),
            by_class: HashMap::new(),
            by_id: HashMap::new(),
        };

        if let Ok(style_sel) = Selector::parse("style") {
            for el in document.select(&style_sel) {
                let css_text: String = el.text().collect();
                sheet.parse_css_text(&css_text);
            }
        }
        sheet
    }

    fn parse_css_text(&mut self, css: &str) {
        let cleaned = strip_css_comments(css);
        for block in cleaned.split('}') {
            let Some((selectors_raw, decls_raw)) = block.split_once('{') else {
                continue;
            };
            // Skip @media / @keyframes blocks safely for now
            if selectors_raw.trim_start().starts_with('@') {
                continue;
            }
            let props = parse_declarations(decls_raw);
            for sel in selectors_raw.split(',') {
                let s = sel.trim();
                if s.is_empty() || s.contains(' ') || s.contains('>') || s.contains(':') {
                    continue;
                }
                if let Some(id) = s.strip_prefix('#') {
                    self.by_id
                        .entry(id.to_ascii_lowercase())
                        .or_default()
                        .merge_from(&props);
                } else if let Some(cls) = s.strip_prefix('.') {
                    self.by_class
                        .entry(cls.to_ascii_lowercase())
                        .or_default()
                        .merge_from(&props);
                } else {
                    self.by_tag
                        .entry(s.to_ascii_lowercase())
                        .or_default()
                        .merge_from(&props);
                }
            }
        }
    }
}

fn strip_css_comments(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    let mut rest = input;
    while let Some(start) = rest.find("/*") {
        out.push_str(&rest[..start]);
        if let Some(end) = rest[start + 2..].find("*/") {
            rest = &rest[start + 2 + end + 2..];
        } else {
            rest = "";
            break;
        }
    }
    out.push_str(rest);
    out
}

fn parse_declarations(decls: &str) -> CssProps {
    let mut props = CssProps::default();
    for decl in decls.split(';') {
        let Some((name_raw, val_raw)) = decl.split_once(':') else {
            continue;
        };
        let name = name_raw.trim().to_ascii_lowercase();
        let val = val_raw.trim().trim_end_matches("!important").trim();
        match name.as_str() {
            "display" => {
                let v = val.to_ascii_lowercase();
                if v == "none" {
                    props.display_none = true;
                } else if v == "flex" || v == "inline-flex" {
                    props.display_flex = Some(FlexDirection::Row);
                }
            }
            "flex-direction" => {
                let v = val.to_ascii_lowercase();
                if v == "column" || v == "column-reverse" {
                    props.display_flex = Some(FlexDirection::Column);
                } else if v == "row" || v == "row-reverse" {
                    props.display_flex = Some(FlexDirection::Row);
                }
            }
            "color" => props.color = parse_css_color(val),
            "background-color" | "background" => {
                if let Some(c) = val.split_whitespace().find_map(parse_css_color) {
                    props.bg_color = Some(c);
                }
            }
            "border-color" => props.border_color = parse_css_color(val),
            "font-size" => {
                if let Some(px) = parse_px(val) {
                    props.font_size = Some(px.clamp(10.0, 40.0));
                }
            }
            "font-weight" => {
                let v = val.to_ascii_lowercase();
                if v == "bold" || v.parse::<u16>().map(|n| n >= 600).unwrap_or(false) {
                    props.bold = Some(true);
                } else if v == "normal" {
                    props.bold = Some(false);
                }
            }
            "font-style" => {
                let v = val.to_ascii_lowercase();
                if v == "italic" || v == "oblique" {
                    props.italic = Some(true);
                } else if v == "normal" {
                    props.italic = Some(false);
                }
            }
            "margin" => {
                if let Some(px) = parse_px(val.split_whitespace().next().unwrap_or("")) {
                    props.margin_top = Some(px);
                    props.margin_bottom = Some(px);
                }
            }
            "margin-top" => props.margin_top = parse_px(val),
            "margin-bottom" => props.margin_bottom = parse_px(val),
            "margin-left" => props.margin_left = parse_px(val),
            "margin-right" => props.margin_right = parse_px(val),
            "padding" => {
                if let Some(px) = parse_px(val.split_whitespace().next().unwrap_or("")) {
                    props.padding_v = Some(px);
                    props.padding_h = Some(px);
                }
            }
            "width" => props.width_px = parse_px(val),
            "height" => props.height_px = parse_px(val),
            _ => {}
        }
    }
    props
}

fn parse_px(val: &str) -> Option<f32> {
    let v = val.trim().to_ascii_lowercase();
    if let Some(num) = v.strip_suffix("px") {
        num.trim().parse::<f32>().ok().filter(|n| n.is_finite() && *n >= 0.0)
    } else if let Some(num) = v.strip_suffix("rem") {
        num.trim().parse::<f32>().ok().map(|n| (n * 16.0).max(0.0))
    } else if let Some(num) = v.strip_suffix("em") {
        num.trim().parse::<f32>().ok().map(|n| (n * 15.0).max(0.0))
    } else if v == "0" {
        Some(0.0)
    } else {
        None
    }
}

fn parse_css_color(val: &str) -> Option<u32> {
    let v = val.trim().to_ascii_lowercase();
    if let Some(hex) = v.strip_prefix('#') {
        match hex.len() {
            3 => {
                let bytes = hex.as_bytes();
                let r = u8::from_str_radix(&format!("{0}{0}", bytes[0] as char), 16).ok()?;
                let g = u8::from_str_radix(&format!("{0}{0}", bytes[1] as char), 16).ok()?;
                let b = u8::from_str_radix(&format!("{0}{0}", bytes[2] as char), 16).ok()?;
                return Some(0xFF00_0000 | ((r as u32) << 16) | ((g as u32) << 8) | (b as u32));
            }
            6 => {
                let rgb = u32::from_str_radix(hex, 16).ok()?;
                return Some(0xFF00_0000 | rgb);
            }
            8 => {
                let rgba = u32::from_str_radix(hex, 16).ok()?;
                let a = rgba & 0xFF;
                let rgb = rgba >> 8;
                return Some((a << 24) | rgb);
            }
            _ => return None,
        }
    }

    if let Some(inner) = v.strip_prefix("rgb(").and_then(|s| s.strip_suffix(')')) {
        let parts: Vec<u8> = inner
            .split(',')
            .filter_map(|p| p.trim().parse::<u8>().ok())
            .collect();
        if parts.len() == 3 {
            return Some(
                0xFF00_0000
                    | ((parts[0] as u32) << 16)
                    | ((parts[1] as u32) << 8)
                    | (parts[2] as u32),
            );
        }
    }

    match v.as_str() {
        "white" => Some(0xFFFF_FFFF),
        "black" => Some(0xFF00_0000),
        "red" => Some(0xFFD9_3025),
        "green" => Some(0xFF18_8038),
        "blue" => Some(0xFF1A_73E8),
        "gray" | "grey" => Some(0xFF80_868B),
        "silver" => Some(0xFFC0_C0C0),
        "yellow" => Some(0xFFFB_BC04),
        "orange" => Some(0xFFF2_9900),
        _ => None,
    }
}

#[derive(Clone)]
struct RenderMeta {
    kind: u8,
    flags: u8,
    font_size: f32,
    fg_color: u32,
    bg_color: u32,
    border_color: u32,
    text: String,
    href: String,
}

enum NodeContext {
    Text { text: String, font_size: f32 },
    Divider,
    Container,
}

struct DisplayCommand {
    kind: u8,
    flags: u8,
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    font_size: f32,
    fg_argb: u32,
    bg_argb: u32,
    border_argb: u32,
    text: String,
    href: String,
}

pub struct WsRenderBuffer {
    pub bytes: Vec<u8>,
}

fn collapse_whitespace(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    let mut prev_space = false;
    for ch in input.chars() {
        if ch.is_whitespace() {
            if !prev_space {
                out.push(' ');
                prev_space = true;
            }
        } else {
            out.push(ch);
            prev_space = false;
        }
    }
    out.trim().to_string()
}

fn measure_wrapped_text(
    text: &str,
    font_size: f32,
    known_width: Option<f32>,
    available_width: AvailableSpace,
) -> Size<f32> {
    let max_width = match (known_width, available_width) {
        (Some(w), _) => w.max(40.0),
        (None, AvailableSpace::Definite(w)) => w.max(40.0),
        (None, AvailableSpace::MinContent) => 120.0,
        (None, AvailableSpace::MaxContent) => 360.0,
    };

    if text.is_empty() {
        return Size::ZERO;
    }

    let avg_char_w = font_size * 0.52;
    let line_height = (font_size * 1.42).ceil();
    let max_chars_per_line = ((max_width / avg_char_w).floor() as usize).max(8);

    let mut line_count: usize = 0;
    let mut max_line_chars: usize = 0;

    for raw_line in text.split('\n') {
        let mut current_len: usize = 0;
        let mut has_word = false;
        for word in raw_line.split_whitespace() {
            has_word = true;
            let w_len = word.chars().count();
            if current_len == 0 {
                if w_len > max_chars_per_line {
                    let wrapped = (w_len + max_chars_per_line - 1) / max_chars_per_line;
                    line_count += wrapped;
                    max_line_chars = max_line_chars.max(max_chars_per_line);
                    current_len = 0;
                } else {
                    current_len = w_len;
                    max_line_chars = max_line_chars.max(current_len);
                }
            } else if current_len + 1 + w_len <= max_chars_per_line {
                current_len += 1 + w_len;
                max_line_chars = max_line_chars.max(current_len);
            } else {
                line_count += 1;
                current_len = w_len.min(max_chars_per_line);
                max_line_chars = max_line_chars.max(current_len);
            }
        }
        if current_len > 0 || !has_word {
            line_count += 1;
        }
    }

    let lines = line_count.max(1) as f32;
    let natural_width = (max_line_chars as f32 * avg_char_w).ceil().min(max_width);
    Size {
        width: known_width.unwrap_or(natural_width.max(1.0)),
        height: lines * line_height,
    }
}

fn is_ignored_tag(tag: &str) -> bool {
    matches!(
        tag,
        "script" | "style" | "head" | "noscript" | "template" | "svg" | "meta" | "link" | "title"
    )
}

fn default_tag_style(tag: &str, palette: &ThemePalette) -> (CssProps, u8) {
    let mut p = CssProps::default();
    let mut flags = 0u8;
    match tag {
        "h1" => {
            p.font_size = Some(24.0);
            p.margin_top = Some(12.0);
            p.margin_bottom = Some(8.0);
            flags |= FLAG_BOLD;
        }
        "h2" => {
            p.font_size = Some(20.0);
            p.margin_top = Some(10.0);
            p.margin_bottom = Some(6.0);
            flags |= FLAG_BOLD;
        }
        "h3" => {
            p.font_size = Some(17.0);
            p.margin_top = Some(8.0);
            p.margin_bottom = Some(4.0);
            flags |= FLAG_BOLD;
        }
        "h4" | "h5" | "h6" | "b" | "strong" => {
            p.font_size = Some(15.0);
            p.margin_top = Some(4.0);
            p.margin_bottom = Some(4.0);
            flags |= FLAG_BOLD;
        }
        "p" | "blockquote" => {
            p.font_size = Some(15.0);
            p.margin_top = Some(4.0);
            p.margin_bottom = Some(8.0);
            if tag == "blockquote" {
                p.padding_h = Some(10.0);
                p.padding_v = Some(6.0);
                p.bg_color = Some(palette.surface_bg);
                p.border_color = Some(palette.link_fg);
                flags |= FLAG_ITALIC;
            }
        }
        "pre" | "code" => {
            p.font_size = Some(13.0);
            p.bg_color = Some(palette.code_bg);
            p.border_color = Some(palette.border_fg);
            p.padding_h = Some(8.0);
            p.padding_v = Some(6.0);
            p.margin_top = Some(4.0);
            p.margin_bottom = Some(6.0);
        }
        "li" => {
            p.font_size = Some(15.0);
            p.margin_left = Some(12.0);
            p.margin_bottom = Some(4.0);
        }
        "a" => {
            p.color = Some(palette.link_fg);
            flags |= FLAG_UNDERLINE;
        }
        "button" => {
            p.bg_color = Some(palette.button_bg);
            p.border_color = Some(palette.border_fg);
            p.padding_h = Some(10.0);
            p.padding_v = Some(6.0);
            p.margin_top = Some(4.0);
            p.margin_bottom = Some(4.0);
            flags |= FLAG_BOLD;
        }
        "div" | "section" | "article" | "main" | "header" | "footer" | "nav" | "ul" | "ol" => {
            p.margin_bottom = Some(4.0);
        }
        _ => {}
    }
    (p, flags)
}

fn resolve_url(base_url: &str, href: &str) -> String {
    let h = href.trim();
    if h.is_empty()
        || h.starts_with("http://")
        || h.starts_with("https://")
        || h.starts_with("javascript:")
        || h.starts_with('#')
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

struct BuildState<'a> {
    tree: TaffyTree<NodeContext>,
    metas: HashMap<NodeId, RenderMeta>,
    sheet: &'a StyleSheet,
    palette: ThemePalette,
    base_url: &'a str,
    dark_mode: bool,
    node_count: usize,
}

impl<'a> BuildState<'a> {
    fn build_element(
        &mut self,
        el_ref: ElementRef<'_>,
        inherited_fg: u32,
        inherited_font: f32,
        inherited_flags: u8,
        inherited_href: &str,
        list_prefix: bool,
    ) -> Option<NodeId> {
        if self.node_count >= MAX_RENDER_NODES {
            return None;
        }

        let el = el_ref.value();
        let tag = el.name().to_ascii_lowercase();
        if is_ignored_tag(&tag) {
            return None;
        }

        let (mut css, mut flags) = default_tag_style(&tag, &self.palette);
        if let Some(tag_rules) = self.sheet.by_tag.get(&tag) {
            css.merge_from(tag_rules);
        }
        for cls in el.classes() {
            if let Some(cls_rules) = self.sheet.by_class.get(&cls.to_ascii_lowercase()) {
                css.merge_from(cls_rules);
            }
        }
        if let Some(id_attr) = el.id() {
            if let Some(id_rules) = self.sheet.by_id.get(&id_attr.to_ascii_lowercase()) {
                css.merge_from(id_rules);
            }
        }
        if let Some(inline) = el.attr("style") {
            css.merge_from(&parse_declarations(inline));
        }
        if css.display_none {
            return None;
        }

        let mut fg = css.color.unwrap_or(inherited_fg);
        // Keep text readable in forced Dark Mode if site sets dark inline color with no dark bg
        if self.dark_mode && css.bg_color.is_none() && is_too_dark(fg) {
            fg = self.palette.text_fg;
        }

        let font_size = css.font_size.unwrap_or(inherited_font);
        if css.bold == Some(true) {
            flags |= FLAG_BOLD;
        } else if css.bold == Some(false) {
            flags &= !FLAG_BOLD;
        }
        if css.italic == Some(true) {
            flags |= FLAG_ITALIC;
        }
        flags |= inherited_flags;

        if tag == "hr" {
            self.node_count += 1;
            let hr_style = Style {
                size: Size {
                    width: percent(1.0),
                    height: length(2.0),
                },
                margin: Rect {
                    left: length(0.0),
                    right: length(0.0),
                    top: length(8.0),
                    bottom: length(8.0),
                },
                ..Default::default()
            };
            let id = self
                .tree
                .new_leaf_with_context(hr_style, NodeContext::Divider)
                .ok()?;
            self.metas.insert(
                id,
                RenderMeta {
                    kind: KIND_DIVIDER,
                    flags: 0,
                    font_size,
                    fg_color: self.palette.border_fg,
                    bg_color: self.palette.border_fg,
                    border_color: 0,
                    text: String::new(),
                    href: String::new(),
                },
            );
            return Some(id);
        }

        if tag == "img" {
            let alt = el.attr("alt").map(collapse_whitespace).unwrap_or_default();
            let label = if alt.is_empty() {
                "[Hình ảnh]".to_string()
            } else {
                format!("[Ảnh: {}]", alt)
            };
            self.node_count += 1;
            let img_style = Style {
                padding: Rect {
                    left: length(8.0),
                    right: length(8.0),
                    top: length(6.0),
                    bottom: length(6.0),
                },
                margin: Rect {
                    left: length(0.0),
                    right: length(0.0),
                    top: length(4.0),
                    bottom: length(6.0),
                },
                ..Default::default()
            };
            let id = self
                .tree
                .new_leaf_with_context(
                    img_style,
                    NodeContext::Text {
                        text: label.clone(),
                        font_size: 13.0,
                    },
                )
                .ok()?;
            self.metas.insert(
                id,
                RenderMeta {
                    kind: if inherited_href.is_empty() {
                        KIND_TEXT
                    } else {
                        KIND_LINK
                    },
                    flags: FLAG_ITALIC,
                    font_size: 13.0,
                    fg_color: self.palette.muted_fg,
                    bg_color: self.palette.surface_bg,
                    border_color: self.palette.border_fg,
                    text: label,
                    href: inherited_href.to_string(),
                },
            );
            return Some(id);
        }

        let resolved_href = if tag == "a" {
            el.attr("href")
                .map(|h| resolve_url(self.base_url, h))
                .unwrap_or_else(|| inherited_href.to_string())
        } else {
            inherited_href.to_string()
        };

        let mut child_ids = Vec::new();
        let mut is_first_li_child = tag == "li";
        for child in el_ref.children() {
            if self.node_count >= MAX_RENDER_NODES {
                break;
            }
            match child.value() {
                Node::Text(t) => {
                    let cleaned = collapse_whitespace(t);
                    if cleaned.is_empty() {
                        continue;
                    }
                    let final_text = if list_prefix || is_first_li_child {
                        is_first_li_child = false;
                        format!("• {}", cleaned)
                    } else {
                        cleaned
                    };
                    if let Some(tid) = self.build_text_leaf(
                        final_text,
                        fg,
                        font_size,
                        flags,
                        &resolved_href,
                    ) {
                        child_ids.push(tid);
                    }
                }
                Node::Element(_) => {
                    if let Some(child_el) = ElementRef::wrap(child) {
                        let pass_prefix = list_prefix || is_first_li_child;
                        if let Some(cid) = self.build_element(
                            child_el,
                            fg,
                            font_size,
                            flags,
                            &resolved_href,
                            pass_prefix,
                        ) {
                            child_ids.push(cid);
                            is_first_li_child = false;
                        }
                    }
                }
                _ => {}
            }
        }

        if child_ids.is_empty() && css.bg_color.is_none() && css.height_px.is_none() {
            return None;
        }

        let (display, flex_direction) = match css.display_flex {
            Some(dir) => (Display::Flex, dir),
            None => (Display::Block, FlexDirection::Column),
        };

        let container_style = Style {
            display,
            flex_direction,
            size: Size {
                width: css.width_px.map(length).unwrap_or(auto()),
                height: css.height_px.map(length).unwrap_or(auto()),
            },
            margin: Rect {
                left: length(css.margin_left.unwrap_or(0.0)),
                right: length(css.margin_right.unwrap_or(0.0)),
                top: length(css.margin_top.unwrap_or(0.0)),
                bottom: length(css.margin_bottom.unwrap_or(0.0)),
            },
            padding: Rect {
                left: length(css.padding_h.unwrap_or(0.0)),
                right: length(css.padding_h.unwrap_or(0.0)),
                top: length(css.padding_v.unwrap_or(0.0)),
                bottom: length(css.padding_v.unwrap_or(0.0)),
            },
            ..Default::default()
        };

        self.node_count += 1;
        let id = self
            .tree
            .new_with_children(container_style, &child_ids)
            .ok()?;
        self.tree
            .set_node_context(id, Some(NodeContext::Container))
            .ok();

        let bg = css.bg_color.unwrap_or(0);
        let border = css.border_color.unwrap_or(0);
        if bg != 0 || border != 0 {
            self.metas.insert(
                id,
                RenderMeta {
                    kind: KIND_BOX,
                    flags: 0,
                    font_size,
                    fg_color: fg,
                    bg_color: bg,
                    border_color: border,
                    text: String::new(),
                    href: String::new(),
                },
            );
        }
        Some(id)
    }

    fn build_text_leaf(
        &mut self,
        text: String,
        fg: u32,
        font_size: f32,
        flags: u8,
        href: &str,
    ) -> Option<NodeId> {
        self.node_count += 1;
        let style = Style {
            size: Size {
                width: auto(),
                height: auto(),
            },
            margin: Rect {
                left: length(0.0),
                right: length(0.0),
                top: length(1.0),
                bottom: length(2.0),
            },
            ..Default::default()
        };
        let id = self
            .tree
            .new_leaf_with_context(
                style,
                NodeContext::Text {
                    text: text.clone(),
                    font_size,
                },
            )
            .ok()?;

        let is_link = !href.is_empty();
        self.metas.insert(
            id,
            RenderMeta {
                kind: if is_link { KIND_LINK } else { KIND_TEXT },
                flags,
                font_size,
                fg_color: fg,
                bg_color: 0,
                border_color: 0,
                text,
                href: href.to_string(),
            },
        );
        Some(id)
    }

    fn collect_commands(&self, node_id: NodeId, offset_x: f32, offset_y: f32, out: &mut Vec<DisplayCommand>) {
        let Ok(layout) = self.tree.layout(node_id) else {
            return;
        };
        let x = offset_x + layout.location.x;
        let y = offset_y + layout.location.y;
        let w = layout.size.width;
        let h = layout.size.height;

        if let Some(meta) = self.metas.get(&node_id) {
            if w > 0.0 && h > 0.0 {
                out.push(DisplayCommand {
                    kind: meta.kind,
                    flags: meta.flags,
                    x,
                    y,
                    w,
                    h,
                    font_size: meta.font_size,
                    fg_argb: meta.fg_color,
                    bg_argb: meta.bg_color,
                    border_argb: meta.border_color,
                    text: meta.text.clone(),
                    href: meta.href.clone(),
                });
            }
        }

        if let Ok(children) = self.tree.children(node_id) {
            for child in children {
                self.collect_commands(child, x, y, out);
            }
        }
    }
}

fn is_too_dark(argb: u32) -> bool {
    let r = (argb >> 16) & 0xFF;
    let g = (argb >> 8) & 0xFF;
    let b = argb & 0xFF;
    (r * 299 + g * 587 + b * 114) / 1000 < 95
}

fn extract_title(document: &Html, fallback: &str) -> String {
    if let Ok(sel) = Selector::parse("title") {
        if let Some(el) = document.select(&sel).next() {
            let t = collapse_whitespace(&el.text().collect::<String>());
            if !t.is_empty() {
                return t;
            }
        }
    }
    fallback.to_string()
}

pub fn render_html_to_display_list(
    html: &str,
    base_url: &str,
    viewport_width: f32,
    dark_mode: bool,
) -> WsRenderBuffer {
    let width = viewport_width.clamp(240.0, 1280.0);
    let palette = ThemePalette::for_mode(dark_mode);
    let document = Html::parse_document(html);
    let title = extract_title(&document, base_url);
    let sheet = StyleSheet::parse(&document);

    let mut state = BuildState {
        tree: TaffyTree::new(),
        metas: HashMap::new(),
        sheet: &sheet,
        palette,
        base_url,
        dark_mode,
        node_count: 0,
    };

    let content_node = state.build_element(
        document.root_element(),
        palette.text_fg,
        15.0,
        0,
        "",
        false,
    );

    let root_style = Style {
        display: Display::Block,
        padding: Rect {
            left: length(12.0),
            right: length(12.0),
            top: length(10.0),
            bottom: length(24.0),
        },
        ..Default::default()
    };

    let root_node = match content_node {
        Some(cid) => state
            .tree
            .new_with_children(root_style, &[cid])
            .expect("taffy root"),
        None => state.tree.new_leaf(root_style).expect("taffy empty root"),
    };

    let _ = state.tree.compute_layout_with_measure(
        root_node,
        Size {
            width: AvailableSpace::Definite(width),
            height: AvailableSpace::MaxContent,
        },
        |inputs, _node_id, node_context, style| {
            taffy::compute_leaf_layout(
                inputs,
                style,
                |_, _| 0.0,
                |known_dimensions, available_space| match node_context {
                    Some(NodeContext::Text { text, font_size }) => {
                        measure_wrapped_text(text, *font_size, known_dimensions.width, available_space.width)
                    }
                    Some(NodeContext::Divider) => Size {
                        width: known_dimensions.width.unwrap_or(width - 24.0),
                        height: known_dimensions.height.unwrap_or(2.0),
                    },
                    _ => Size::ZERO,
                },
            )
        },
    );

    let content_height = state
        .tree
        .layout(root_node)
        .map(|l| l.size.height)
        .unwrap_or(120.0)
        .max(120.0);

    let mut commands = Vec::new();
    // Base background box for the full page
    commands.push(DisplayCommand {
        kind: KIND_BOX,
        flags: 0,
        x: 0.0,
        y: 0.0,
        w: width,
        h: content_height,
        font_size: 15.0,
        fg_argb: palette.text_fg,
        bg_argb: palette.page_bg,
        border_argb: 0,
        text: String::new(),
        href: String::new(),
    });
    state.collect_commands(root_node, 0.0, 0.0, &mut commands);

    serialize_display_list(&title, width, content_height, &commands)
}

fn serialize_display_list(
    title: &str,
    content_width: f32,
    content_height: f32,
    commands: &[DisplayCommand],
) -> WsRenderBuffer {
    let mut bytes = Vec::with_capacity(64 + commands.len() * 48);
    bytes.extend_from_slice(&RENDER_MAGIC.to_le_bytes());
    bytes.extend_from_slice(&content_width.to_le_bytes());
    bytes.extend_from_slice(&content_height.to_le_bytes());

    let title_bytes = title.as_bytes();
    bytes.extend_from_slice(&(title_bytes.len() as u32).to_le_bytes());
    bytes.extend_from_slice(title_bytes);

    bytes.extend_from_slice(&(commands.len() as u32).to_le_bytes());
    for cmd in commands {
        bytes.push(cmd.kind);
        bytes.push(cmd.flags);
        bytes.extend_from_slice(&cmd.x.to_le_bytes());
        bytes.extend_from_slice(&cmd.y.to_le_bytes());
        bytes.extend_from_slice(&cmd.w.to_le_bytes());
        bytes.extend_from_slice(&cmd.h.to_le_bytes());
        bytes.extend_from_slice(&cmd.font_size.to_le_bytes());
        bytes.extend_from_slice(&cmd.fg_argb.to_le_bytes());
        bytes.extend_from_slice(&cmd.bg_argb.to_le_bytes());
        bytes.extend_from_slice(&cmd.border_argb.to_le_bytes());

        let txt = cmd.text.as_bytes();
        bytes.extend_from_slice(&(txt.len() as u32).to_le_bytes());
        bytes.extend_from_slice(txt);

        let href = cmd.href.as_bytes();
        bytes.extend_from_slice(&(href.len() as u32).to_le_bytes());
        bytes.extend_from_slice(href);
    }

    WsRenderBuffer { bytes }
}
