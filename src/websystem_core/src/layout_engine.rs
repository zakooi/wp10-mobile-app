use scraper::{ElementRef, Html, Node, Selector};
use std::collections::HashMap;
use taffy::prelude::*;

const RENDER_MAGIC_V2: u32 = 0x5753_5232; // "WSR2"
const MAX_RENDER_NODES: usize = 4000;

const FLAG_BOLD: u8 = 1;
const FLAG_ITALIC: u8 = 2;
const FLAG_UNDERLINE: u8 = 4;
const FLAG_PASSWORD: u8 = 8;
const FLAG_MULTILINE: u8 = 16;
const FLAG_CENTER: u8 = 32;
const FLAG_CHECKED: u8 = 64;

const KIND_BOX: u8 = 0;
const KIND_TEXT: u8 = 1;
const KIND_LINK: u8 = 2;
const KIND_DIVIDER: u8 = 3;
const KIND_IMAGE: u8 = 4;
const KIND_INPUT: u8 = 5;
const KIND_BUTTON: u8 = 6;
const KIND_HIDDEN: u8 = 7;

pub struct RenderOptions {
    pub viewport_width: f32,
    pub dark_mode: bool,
    pub block_ads: bool,
    pub block_images: bool,
    pub reader_mode: bool,
}

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
    input_bg: u32,
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
                border_fg: 0xFF3C_4043,
                code_bg: 0xFF22_252A,
                button_bg: 0xFF2B_4C7E,
                input_bg: 0xFF24_2424,
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
                button_bg: 0xFF1A_73E8,
                input_bg: 0xFFFF_FFFF,
            }
        }
    }
}

#[derive(Clone, Default)]
struct CssProps {
    display_none: bool,
    display_flex: Option<FlexDirection>,
    display_inline: Option<bool>,
    flex_wrap: Option<FlexWrap>,
    justify_content: Option<JustifyContent>,
    align_items: Option<AlignItems>,
    gap_px: Option<f32>,
    color: Option<u32>,
    bg_color: Option<u32>,
    border_color: Option<u32>,
    font_size: Option<f32>,
    bold: Option<bool>,
    italic: Option<bool>,
    underline: Option<bool>,
    text_center: Option<bool>,
    margin_top: Option<f32>,
    margin_bottom: Option<f32>,
    margin_left: Option<f32>,
    margin_right: Option<f32>,
    padding_top: Option<f32>,
    padding_bottom: Option<f32>,
    padding_left: Option<f32>,
    padding_right: Option<f32>,
    width_px: Option<f32>,
    height_px: Option<f32>,
    min_width_px: Option<f32>,
    min_height_px: Option<f32>,
    max_width_px: Option<f32>,
    flex_grow: Option<f32>,
}

impl CssProps {
    fn merge_from(&mut self, other: &CssProps) {
        if other.display_none {
            self.display_none = true;
        }
        if other.display_flex.is_some() {
            self.display_flex = other.display_flex;
        }
        if other.display_inline.is_some() {
            self.display_inline = other.display_inline;
        }
        if other.flex_wrap.is_some() {
            self.flex_wrap = other.flex_wrap;
        }
        if other.justify_content.is_some() {
            self.justify_content = other.justify_content;
        }
        if other.align_items.is_some() {
            self.align_items = other.align_items;
        }
        if other.gap_px.is_some() {
            self.gap_px = other.gap_px;
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
        if other.underline.is_some() {
            self.underline = other.underline;
        }
        if other.text_center.is_some() {
            self.text_center = other.text_center;
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
        if other.padding_top.is_some() {
            self.padding_top = other.padding_top;
        }
        if other.padding_bottom.is_some() {
            self.padding_bottom = other.padding_bottom;
        }
        if other.padding_left.is_some() {
            self.padding_left = other.padding_left;
        }
        if other.padding_right.is_some() {
            self.padding_right = other.padding_right;
        }
        if other.width_px.is_some() {
            self.width_px = other.width_px;
        }
        if other.height_px.is_some() {
            self.height_px = other.height_px;
        }
        if other.min_width_px.is_some() {
            self.min_width_px = other.min_width_px;
        }
        if other.min_height_px.is_some() {
            self.min_height_px = other.min_height_px;
        }
        if other.max_width_px.is_some() {
            self.max_width_px = other.max_width_px;
        }
        if other.flex_grow.is_some() {
            self.flex_grow = other.flex_grow;
        }
    }
}

const MAX_COMPLEX_CSS_RULES: usize = 256;

struct StyleSheet {
    by_tag: HashMap<String, CssProps>,
    by_class: HashMap<String, CssProps>,
    by_id: HashMap<String, CssProps>,
    complex_rules: Vec<(String, CssProps)>,
}

impl StyleSheet {
    fn parse(document: &Html, extra_css: &str) -> Self {
        let mut sheet = Self {
            by_tag: HashMap::new(),
            by_class: HashMap::new(),
            by_id: HashMap::new(),
            complex_rules: Vec::new(),
        };

        if !extra_css.trim().is_empty() {
            sheet.parse_css_text(extra_css);
        }

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
        self.parse_css_block_list(&cleaned);
    }

    fn parse_css_block_list(&mut self, input: &str) {
        let bytes = input.as_bytes();
        let mut idx = 0usize;
        let len = bytes.len();

        while idx < len {
            while idx < len && bytes[idx].is_ascii_whitespace() {
                idx += 1;
            }
            if idx >= len {
                break;
            }

            let sel_start = idx;
            while idx < len && bytes[idx] != b'{' && bytes[idx] != b';' {
                idx += 1;
            }
            if idx >= len {
                break;
            }
            if bytes[idx] == b';' {
                idx += 1;
                continue;
            }

            let header = input[sel_start..idx].trim();
            idx += 1; // skip '{'

            let body_start = idx;
            let mut depth = 1usize;
            while idx < len && depth > 0 {
                if bytes[idx] == b'{' {
                    depth += 1;
                } else if bytes[idx] == b'}' {
                    depth -= 1;
                    if depth == 0 {
                        break;
                    }
                }
                idx += 1;
            }
            let body_end = idx;
            if idx < len && bytes[idx] == b'}' {
                idx += 1;
            }

            let body = &input[body_start..body_end];
            if header.starts_with('@') {
                let h_low = header.to_ascii_lowercase();
                if h_low.starts_with("@media")
                    && !h_low.contains("print")
                    && !h_low.contains("prefers-color-scheme: dark")
                {
                    self.parse_css_block_list(body);
                }
                continue;
            }

            let props = parse_declarations(body);
            for sel in header.split(',') {
                let s = sel.trim();
                if s.is_empty()
                    || s.contains('+')
                    || s.contains('~')
                    || s.contains(':')
                    || s == "*"
                {
                    continue;
                }

                let is_complex = s.contains(' ') || s.contains('>') || s.contains('[');
                if is_complex {
                    if self.complex_rules.len() < MAX_COMPLEX_CSS_RULES {
                        self.complex_rules.push((s.to_string(), props.clone()));
                    }
                    continue;
                }

                if let Some(id) = s.strip_prefix('#') {
                    if !id.contains('.') {
                        self.by_id
                            .entry(id.to_ascii_lowercase())
                            .or_default()
                            .merge_from(&props);
                    } else if self.complex_rules.len() < MAX_COMPLEX_CSS_RULES {
                        self.complex_rules.push((s.to_string(), props.clone()));
                    }
                } else if let Some(cls) = s.strip_prefix('.') {
                    if !cls.contains('.') && !cls.is_empty() {
                        self.by_class
                            .entry(cls.to_ascii_lowercase())
                            .or_default()
                            .merge_from(&props);
                    } else if !cls.is_empty() && self.complex_rules.len() < MAX_COMPLEX_CSS_RULES {
                        self.complex_rules.push((s.to_string(), props.clone()));
                    }
                } else if s.contains('.') || s.contains('#') {
                    if self.complex_rules.len() < MAX_COMPLEX_CSS_RULES {
                        self.complex_rules.push((s.to_string(), props.clone()));
                    }
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

fn parse_box_edges(val: &str) -> Option<(f32, f32, f32, f32)> {
    let parts: Vec<f32> = val.split_whitespace().filter_map(parse_px).collect();
    match parts.len() {
        1 => Some((parts[0], parts[0], parts[0], parts[0])),
        2 => Some((parts[0], parts[1], parts[0], parts[1])),
        3 => Some((parts[0], parts[1], parts[2], parts[1])),
        4 => Some((parts[0], parts[1], parts[2], parts[3])),
        _ => None,
    }
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
                    props.display_inline = Some(false);
                } else if v == "grid" || v == "inline-grid" {
                    props.display_flex = Some(FlexDirection::Row);
                    props.flex_wrap = Some(FlexWrap::Wrap);
                    props.display_inline = Some(false);
                } else if v == "inline" || v == "inline-block" {
                    props.display_inline = Some(true);
                } else if v == "block" {
                    props.display_inline = Some(false);
                }
            }
            "flex" => {
                if let Some(first) = val.split_whitespace().next() {
                    if let Ok(grow) = first.parse::<f32>() {
                        if grow.is_finite() && grow >= 0.0 {
                            props.flex_grow = Some(grow.min(10.0));
                        }
                    }
                }
            }
            "flex-grow" => {
                if let Ok(grow) = val.trim().parse::<f32>() {
                    if grow.is_finite() && grow >= 0.0 {
                        props.flex_grow = Some(grow.min(10.0));
                    }
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
            "flex-wrap" => {
                let v = val.to_ascii_lowercase();
                if v == "wrap" {
                    props.flex_wrap = Some(FlexWrap::Wrap);
                } else if v == "nowrap" {
                    props.flex_wrap = Some(FlexWrap::NoWrap);
                }
            }
            "justify-content" => {
                let v = val.to_ascii_lowercase();
                props.justify_content = match v.as_str() {
                    "center" => Some(JustifyContent::Center),
                    "space-between" => Some(JustifyContent::SpaceBetween),
                    "space-around" => Some(JustifyContent::SpaceAround),
                    "flex-end" | "end" => Some(JustifyContent::FlexEnd),
                    "flex-start" | "start" => Some(JustifyContent::FlexStart),
                    _ => None,
                };
            }
            "align-items" => {
                let v = val.to_ascii_lowercase();
                props.align_items = match v.as_str() {
                    "center" => Some(AlignItems::Center),
                    "flex-start" | "start" => Some(AlignItems::FlexStart),
                    "flex-end" | "end" => Some(AlignItems::FlexEnd),
                    "stretch" => Some(AlignItems::Stretch),
                    _ => None,
                };
            }
            "gap" | "grid-gap" => {
                if let Some(px) = parse_px(val.split_whitespace().next().unwrap_or("")) {
                    props.gap_px = Some(px.clamp(0.0, 48.0));
                }
            }
            "color" => props.color = parse_css_color(val),
            "background-color" | "background" => {
                if let Some(c) = find_css_color_in_value(val) {
                    props.bg_color = Some(c);
                }
            }
            "border" | "border-top" | "border-bottom" => {
                if let Some(c) = find_css_color_in_value(val) {
                    props.border_color = Some(c);
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
            "text-decoration" => {
                let v = val.to_ascii_lowercase();
                if v.contains("underline") {
                    props.underline = Some(true);
                } else if v == "none" {
                    props.underline = Some(false);
                }
            }
            "text-align" => {
                let v = val.to_ascii_lowercase();
                if v == "center" {
                    props.text_center = Some(true);
                } else if v == "left" {
                    props.text_center = Some(false);
                }
            }
            "margin" => {
                if let Some((t, r, b, l)) = parse_box_edges(val) {
                    props.margin_top = Some(t);
                    props.margin_right = Some(r);
                    props.margin_bottom = Some(b);
                    props.margin_left = Some(l);
                }
            }
            "margin-top" => props.margin_top = parse_px(val),
            "margin-bottom" => props.margin_bottom = parse_px(val),
            "margin-left" => props.margin_left = parse_px(val),
            "margin-right" => props.margin_right = parse_px(val),
            "padding" => {
                if let Some((t, r, b, l)) = parse_box_edges(val) {
                    props.padding_top = Some(t);
                    props.padding_right = Some(r);
                    props.padding_bottom = Some(b);
                    props.padding_left = Some(l);
                }
            }
            "padding-top" => props.padding_top = parse_px(val),
            "padding-bottom" => props.padding_bottom = parse_px(val),
            "padding-left" => props.padding_left = parse_px(val),
            "padding-right" => props.padding_right = parse_px(val),
            "width" => props.width_px = parse_px(val),
            "height" => props.height_px = parse_px(val),
            "min-width" => props.min_width_px = parse_px(val),
            "min-height" => props.min_height_px = parse_px(val),
            "max-width" => props.max_width_px = parse_px(val),
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
    } else if let Some(num) = v.strip_suffix("pt") {
        num.trim().parse::<f32>().ok().map(|n| (n * 1.333).max(0.0))
    } else if v == "0" {
        Some(0.0)
    } else {
        None
    }
}

fn parse_html_dim(val: Option<&str>) -> Option<f32> {
    let s = val?.trim();
    if s.ends_with('%') {
        return None;
    }
    let clean = s.trim_end_matches("px").trim();
    clean.parse::<f32>().ok().filter(|n| n.is_finite() && *n > 0.0)
}

fn el_matches_simple(el_ref: ElementRef<'_>, token: &str) -> bool {
    let t = token.trim();
    if t.is_empty() {
        return false;
    }
    if t == "*" {
        return true;
    }
    let el = el_ref.value();

    let (base, attr_cond) = if let Some(b_start) = t.find('[') {
        if let Some(b_end) = t[b_start..].find(']') {
            (&t[..b_start], Some(&t[b_start + 1..b_start + b_end]))
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
            if el.attr(&ak_low) != Some(av_clean) {
                return false;
            }
        } else {
            let ak_low = cond.trim().to_ascii_lowercase();
            if el.attr(&ak_low).is_none() {
                return false;
            }
        }
    }

    if base.is_empty() {
        return true;
    }

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
        if !el.name().eq_ignore_ascii_case(rt) {
            return false;
        }
    }
    if let Some(ri) = req_id {
        if el.id() != Some(ri) {
            return false;
        }
    }
    for rc in req_classes {
        if !el.classes().any(|c| c == rc) {
            return false;
        }
    }
    true
}

fn el_matches_complex(el_ref: ElementRef<'_>, selector: &str) -> bool {
    let normalized = selector.replace('>', " > ");
    let tokens: Vec<&str> = normalized.split_whitespace().collect();
    if tokens.is_empty() {
        return false;
    }
    let Some(&last) = tokens.last() else {
        return false;
    };
    if last == ">" || !el_matches_simple(el_ref, last) {
        return false;
    }

    let mut cur = el_ref;
    let mut direct_child = false;
    for &tok in tokens[..tokens.len() - 1].iter().rev() {
        if tok == ">" {
            direct_child = true;
            continue;
        }
        if direct_child {
            let Some(parent_el) = cur.parent().and_then(ElementRef::wrap) else {
                return false;
            };
            if !el_matches_simple(parent_el, tok) {
                return false;
            }
            cur = parent_el;
            direct_child = false;
        } else {
            let mut matched = false;
            let mut next_p = cur.parent().and_then(ElementRef::wrap);
            while let Some(parent_el) = next_p {
                if el_matches_simple(parent_el, tok) {
                    cur = parent_el;
                    matched = true;
                    break;
                }
                next_p = parent_el.parent().and_then(ElementRef::wrap);
            }
            if !matched {
                return false;
            }
        }
    }
    true
}

fn find_css_color_in_value(val: &str) -> Option<u32> {
    if let Some(c) = parse_css_color(val) {
        return Some(c);
    }
    let low = val.to_ascii_lowercase();
    for prefix in ["rgba(", "rgb("] {
        if let Some(start) = low.find(prefix) {
            if let Some(end_rel) = low[start..].find(')') {
                if let Some(c) = parse_css_color(&low[start..start + end_rel + 1]) {
                    return Some(c);
                }
            }
        }
    }
    val.split_whitespace().find_map(parse_css_color)
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

    if let Some(inner) = v.strip_prefix("rgba(").and_then(|s| s.strip_suffix(')')) {
        let parts: Vec<&str> = inner.split(',').map(|p| p.trim()).collect();
        if parts.len() == 4 {
            let r = parts[0].parse::<u8>().ok()?;
            let g = parts[1].parse::<u8>().ok()?;
            let b = parts[2].parse::<u8>().ok()?;
            let a_f = parts[3].parse::<f32>().ok()?.clamp(0.0, 1.0);
            let a = (a_f * 255.0).round() as u32;
            return Some((a << 24) | ((r as u32) << 16) | ((g as u32) << 8) | (b as u32));
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
    form_id: i32,
    text: String,
    href: String,
    extra: String,
}

enum NodeContext {
    Text { text: String, font_size: f32, bold: bool },
    Fixed { width: f32, height: f32 },
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
    form_id: i32,
    text: String,
    href: String,
    extra: String,
}

pub struct WsRenderBuffer {
    pub bytes: Vec<u8>,
    pub js_scripts: u32,
    pub js_errors: u32,
}

fn sanitize_extra(input: &str) -> String {
    input
        .chars()
        .map(|c| match c {
            '\t' | '\r' | '\n' | '\x1e' | '\x1f' => ' ',
            _ => c,
        })
        .collect::<String>()
        .trim()
        .to_string()
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
    bold: bool,
    known_width: Option<f32>,
    available_width: AvailableSpace,
    viewport_max: f32,
) -> Size<f32> {
    let max_width = match (known_width, available_width) {
        (Some(w), _) => w.max(24.0),
        (None, AvailableSpace::Definite(w)) => w.max(24.0),
        (None, AvailableSpace::MinContent) => 80.0,
        (None, AvailableSpace::MaxContent) => viewport_max.max(200.0),
    };

    if text.is_empty() {
        return Size::ZERO;
    }

    let avg_char_w = font_size * if bold { 0.63 } else { 0.58 };
    let line_height = (font_size * 1.48).ceil();
    let max_chars_per_line = ((max_width / avg_char_w).floor() as usize).max(6);

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
    let natural_width = (max_line_chars as f32 * avg_char_w + 8.0).ceil().min(max_width);
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

fn is_inline_tag(tag: &str) -> bool {
    matches!(
        tag,
        "a" | "b"
            | "strong"
            | "i"
            | "em"
            | "u"
            | "span"
            | "code"
            | "small"
            | "mark"
            | "abbr"
            | "label"
            | "sub"
            | "sup"
            | "time"
            | "cite"
            | "q"
            | "button"
            | "input"
            | "select"
            | "br"
            | "img"
    )
}

fn is_ad_or_cookie_element(el: &scraper::node::Element) -> bool {
    if let Some(id) = el.id() {
        let id_low = id.to_ascii_lowercase();
        if id_low.contains("google_ads")
            || id_low.contains("taboola-")
            || id_low.contains("outbrain-")
            || id_low.contains("ad-wrapper")
            || id_low == "onetrust-consent-sdk"
            || id_low == "cookie-law-info-bar"
            || id_low.contains("cookie-notice")
        {
            return true;
        }
    }
    for cls in el.classes() {
        let c = cls.to_ascii_lowercase();
        if c == "adsbygoogle"
            || c.contains("taboola-")
            || c.contains("outbrain-")
            || c.contains("banner-ads")
            || c.contains("ad-container")
            || c == "cookie-banner"
            || c.contains("cookie-consent")
            || c == "qc-cmp-ui-container"
            || c == "cc-window"
        {
            return true;
        }
    }
    false
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
            p.margin_top = Some(2.0);
            p.margin_bottom = Some(2.0);
            flags |= FLAG_BOLD;
        }
        "i" | "em" | "cite" => {
            flags |= FLAG_ITALIC;
        }
        "u" => {
            flags |= FLAG_UNDERLINE;
        }
        "p" | "blockquote" => {
            p.font_size = Some(15.0);
            p.margin_top = Some(4.0);
            p.margin_bottom = Some(8.0);
            if tag == "blockquote" {
                p.padding_left = Some(10.0);
                p.padding_right = Some(10.0);
                p.padding_top = Some(6.0);
                p.padding_bottom = Some(6.0);
                p.bg_color = Some(palette.surface_bg);
                p.border_color = Some(palette.link_fg);
                flags |= FLAG_ITALIC;
            }
        }
        "pre" | "code" => {
            p.font_size = Some(13.0);
            p.bg_color = Some(palette.code_bg);
            p.border_color = Some(palette.border_fg);
            p.padding_left = Some(6.0);
            p.padding_right = Some(6.0);
            p.padding_top = Some(4.0);
            p.padding_bottom = Some(4.0);
            if tag == "pre" {
                p.margin_top = Some(4.0);
                p.margin_bottom = Some(6.0);
            }
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
            p.color = Some(0xFFFF_FFFF);
            p.padding_left = Some(10.0);
            p.padding_right = Some(10.0);
            p.padding_top = Some(6.0);
            p.padding_bottom = Some(6.0);
            flags |= FLAG_BOLD | FLAG_CENTER;
        }
        "table" => {
            p.margin_top = Some(6.0);
            p.margin_bottom = Some(8.0);
            p.border_color = Some(palette.border_fg);
        }
        "tr" => {
            p.display_flex = Some(FlexDirection::Row);
            p.flex_wrap = Some(FlexWrap::NoWrap);
            p.gap_px = Some(2.0);
            p.margin_bottom = Some(2.0);
        }
        "td" | "th" => {
            p.padding_left = Some(6.0);
            p.padding_right = Some(6.0);
            p.padding_top = Some(4.0);
            p.padding_bottom = Some(4.0);
            p.border_color = Some(palette.border_fg);
            if tag == "th" {
                p.bg_color = Some(palette.surface_bg);
                flags |= FLAG_BOLD;
            }
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
        || h.starts_with("data:")
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

pub fn extract_stylesheet_urls(html: &str, base_url: &str) -> Vec<String> {
    let document = Html::parse_document(html);
    let mut urls = Vec::new();
    let Ok(sel) = Selector::parse("link[rel]") else {
        return urls;
    };

    for el in document.select(&sel) {
        let rel = el.value().attr("rel").unwrap_or("").to_ascii_lowercase();
        if !rel.split_whitespace().any(|r| r == "stylesheet") {
            continue;
        }
        if let Some(href) = el.value().attr("href") {
            let resolved = resolve_url(base_url, href);
            if (resolved.starts_with("http://") || resolved.starts_with("https://"))
                && !urls.iter().any(|u| u == &resolved)
            {
                urls.push(resolved);
                if urls.len() >= 6 {
                    break;
                }
            }
        }
    }
    urls
}

#[derive(Clone)]
struct FormContext {
    form_id: i32,
    action: String,
    method: String,
}

struct BuildState<'a> {
    tree: TaffyTree<NodeContext>,
    metas: HashMap<NodeId, RenderMeta>,
    hidden_commands: Vec<DisplayCommand>,
    sheet: &'a StyleSheet,
    palette: ThemePalette,
    base_url: &'a str,
    viewport_content_w: f32,
    dark_mode: bool,
    block_ads: bool,
    block_images: bool,
    node_count: usize,
    next_form_id: i32,
}

impl<'a> BuildState<'a> {
    fn flush_inline_run(&mut self, inline_run: &mut Vec<NodeId>, out_children: &mut Vec<NodeId>) {
        if inline_run.is_empty() {
            return;
        }
        if inline_run.len() == 1 {
            out_children.push(inline_run.remove(0));
            return;
        }

        let row_style = Style {
            display: Display::Flex,
            flex_direction: FlexDirection::Row,
            flex_wrap: FlexWrap::Wrap,
            align_items: Some(AlignItems::Center),
            gap: Size {
                width: length(4.0_f32),
                height: length(2.0_f32),
            },
            margin: Rect {
                left: length(0.0_f32),
                right: length(0.0_f32),
                top: length(0.0_f32),
                bottom: length(3.0_f32),
            },
            ..Default::default()
        };

        self.node_count += 1;
        if let Ok(row_id) = self.tree.new_with_children(row_style, inline_run.as_slice()) {
            let _ = self.tree.set_node_context(row_id, Some(NodeContext::Container));
            out_children.push(row_id);
        }
        inline_run.clear();
    }

    fn build_element(
        &mut self,
        el_ref: ElementRef<'_>,
        inherited_fg: u32,
        inherited_font: f32,
        inherited_flags: u8,
        inherited_href: &str,
        list_prefix: bool,
        form_ctx: Option<&FormContext>,
        in_table_row: bool,
        inherited_light_bg: bool,
    ) -> Option<NodeId> {
        if self.node_count >= MAX_RENDER_NODES {
            return None;
        }

        let el = el_ref.value();
        let tag = el.name().to_ascii_lowercase();
        if is_ignored_tag(&tag) {
            return None;
        }
        if self.block_ads && is_ad_or_cookie_element(el) {
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
        for (sel, c_rules) in &self.sheet.complex_rules {
            if el_matches_complex(el_ref, sel) {
                css.merge_from(c_rules);
            }
        }
        if let Some(inline) = el.attr("style") {
            css.merge_from(&parse_declarations(inline));
        }
        if css.display_none {
            return None;
        }

        let next_light_bg = match css.bg_color.filter(|c| (*c >> 24) > 128) {
            Some(bg) => !is_too_dark(bg),
            None => inherited_light_bg,
        };
        let mut fg = css.color.unwrap_or(inherited_fg);
        if self.dark_mode && !next_light_bg && is_too_dark(fg) {
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
        } else if css.italic == Some(false) {
            flags &= !FLAG_ITALIC;
        }
        if css.underline == Some(true) {
            flags |= FLAG_UNDERLINE;
        } else if css.underline == Some(false) {
            flags &= !FLAG_UNDERLINE;
        }
        if css.text_center == Some(true) {
            flags |= FLAG_CENTER;
        }
        flags |= inherited_flags;

        // Horizontal rule <hr>
        if tag == "hr" {
            self.node_count += 1;
            let hr_style = Style {
                size: Size {
                    width: percent(1.0_f32),
                    height: length(2.0_f32),
                },
                margin: Rect {
                    left: length(0.0_f32),
                    right: length(0.0_f32),
                    top: length(8.0_f32),
                    bottom: length(8.0_f32),
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
                    form_id: -1,
                    text: String::new(),
                    href: String::new(),
                    extra: String::new(),
                },
            );
            return Some(id);
        }

        // Image <img>
        if tag == "img" {
            if self.block_images {
                return None;
            }
            let src_raw = el.attr("src").or_else(|| el.attr("data-src")).unwrap_or("");
            let src = resolve_url(self.base_url, src_raw);
            let alt = el.attr("alt").map(collapse_whitespace).unwrap_or_default();

            let attr_w = parse_html_dim(el.attr("width")).or(css.width_px);
            let attr_h = parse_html_dim(el.attr("height")).or(css.height_px);
            if matches!((attr_w, attr_h), (Some(w), Some(h)) if w <= 2.0 && h <= 2.0) {
                return None; // skip 1x1 tracking pixels
            }

            let max_img_w = (self.viewport_content_w - 8.0).max(80.0);
            let (img_w, img_h) = match (attr_w, attr_h) {
                (Some(w), Some(h)) => {
                    if w > max_img_w {
                        let scale = max_img_w / w;
                        (max_img_w, (h * scale).max(16.0))
                    } else {
                        (w.max(16.0), h.max(16.0))
                    }
                }
                (Some(w), None) => {
                    let clamped_w = w.min(max_img_w).max(24.0);
                    (clamped_w, (clamped_w * 0.6).clamp(24.0, 220.0))
                }
                (None, Some(h)) => {
                    let clamped_h = h.clamp(18.0, 240.0);
                    ((clamped_h * 1.4).min(max_img_w), clamped_h)
                }
                (None, None) => {
                    let w = max_img_w.min(240.0);
                    (w, 136.0)
                }
            };

            let label = if alt.is_empty() {
                "[Hình ảnh]".to_string()
            } else {
                alt.clone()
            };

            self.node_count += 1;
            let img_style = Style {
                size: Size {
                    width: length(img_w),
                    height: length(img_h),
                },
                flex_shrink: 0.0,
                margin: Rect {
                    left: length(css.margin_left.unwrap_or(0.0)),
                    right: length(css.margin_right.unwrap_or(4.0)),
                    top: length(css.margin_top.unwrap_or(4.0)),
                    bottom: length(css.margin_bottom.unwrap_or(6.0)),
                },
                ..Default::default()
            };
            let id = self
                .tree
                .new_leaf_with_context(
                    img_style,
                    NodeContext::Fixed {
                        width: img_w,
                        height: img_h,
                    },
                )
                .ok()?;
            self.metas.insert(
                id,
                RenderMeta {
                    kind: KIND_IMAGE,
                    flags: FLAG_ITALIC | FLAG_CENTER,
                    font_size: 12.0,
                    fg_color: self.palette.muted_fg,
                    bg_color: self.palette.surface_bg,
                    border_color: self.palette.border_fg,
                    form_id: -1,
                    text: label,
                    href: src,
                    extra: format!("{}\t{}", inherited_href, alt),
                },
            );
            return Some(id);
        }

        // Form inputs <input>, <textarea>, <select>
        if tag == "input" {
            let input_type = el
                .attr("type")
                .unwrap_or("text")
                .trim()
                .to_ascii_lowercase();
            let name = el.attr("name").unwrap_or("").trim().to_string();
            let value = el.attr("value").unwrap_or("").to_string();
            let placeholder = el.attr("placeholder").unwrap_or("").trim().to_string();

            let (fid, action_method) = match form_ctx {
                Some(fc) => (fc.form_id, format!("{}|{}", fc.action, fc.method)),
                None => (-1, format!("{}|GET", self.base_url)),
            };

            if input_type == "hidden" {
                self.hidden_commands.push(DisplayCommand {
                    kind: KIND_HIDDEN,
                    flags: 0,
                    x: 0.0,
                    y: 0.0,
                    w: 0.0,
                    h: 0.0,
                    font_size: 14.0,
                    fg_argb: 0,
                    bg_argb: 0,
                    border_argb: 0,
                    form_id: fid,
                    text: value,
                    href: action_method,
                    extra: format!("{}\t\thidden", name),
                });
                return None;
            }

            if input_type == "submit" || input_type == "button" || input_type == "reset" {
                let label = if value.trim().is_empty() {
                    if input_type == "reset" {
                        "Đặt lại".to_string()
                    } else {
                        "Tìm / Gửi".to_string()
                    }
                } else {
                    value.trim().to_string()
                };
                return self.build_button_leaf(
                    label,
                    fid,
                    &action_method,
                    &name,
                    &value,
                    &input_type,
                    css.bg_color.unwrap_or(self.palette.button_bg),
                    css.color.unwrap_or(0xFFFF_FFFF),
                );
            }

            if input_type == "checkbox" || input_type == "radio" {
                let checked = el.attr("checked").is_some();
                let submit_val = if value.trim().is_empty() {
                    "on".to_string()
                } else {
                    value.trim().to_string()
                };
                let box_size = 24.0_f32;
                self.node_count += 1;
                let style = Style {
                    size: Size {
                        width: length(box_size),
                        height: length(box_size),
                    },
                    flex_shrink: 0.0,
                    margin: Rect {
                        left: length(0.0_f32),
                        right: length(6.0_f32),
                        top: length(4.0_f32),
                        bottom: length(4.0_f32),
                    },
                    ..Default::default()
                };
                let id = self
                    .tree
                    .new_leaf_with_context(
                        style,
                        NodeContext::Fixed {
                            width: box_size,
                            height: box_size,
                        },
                    )
                    .ok()?;
                self.metas.insert(
                    id,
                    RenderMeta {
                        kind: KIND_INPUT,
                        flags: if checked { FLAG_CHECKED } else { 0 },
                        font_size: 14.0,
                        fg_color: self.palette.link_fg,
                        bg_color: self.palette.input_bg,
                        border_color: self.palette.border_fg,
                        form_id: fid,
                        text: submit_val.clone(),
                        href: action_method,
                        extra: format!(
                            "{}\t{}\t{}",
                            sanitize_extra(&name),
                            sanitize_extra(&submit_val),
                            input_type
                        ),
                    },
                );
                return Some(id);
            }

            let box_w = css
                .width_px
                .unwrap_or(self.viewport_content_w.min(260.0))
                .clamp(90.0, self.viewport_content_w.max(120.0));
            let box_h = 38.0_f32;
            let mut input_flags = 0u8;
            if input_type == "password" {
                input_flags |= FLAG_PASSWORD;
            }

            self.node_count += 1;
            let style = Style {
                size: Size {
                    width: length(box_w),
                    height: length(box_h),
                },
                flex_shrink: 1.0,
                max_size: Size {
                    width: percent(1.0_f32),
                    height: auto(),
                },
                margin: Rect {
                    left: length(0.0_f32),
                    right: length(4.0_f32),
                    top: length(4.0_f32),
                    bottom: length(6.0_f32),
                },
                ..Default::default()
            };
            let id = self
                .tree
                .new_leaf_with_context(
                    style,
                    NodeContext::Fixed {
                        width: box_w,
                        height: box_h,
                    },
                )
                .ok()?;
            self.metas.insert(
                id,
                RenderMeta {
                    kind: KIND_INPUT,
                    flags: input_flags,
                    font_size: 14.0,
                    fg_color: self.palette.text_fg,
                    bg_color: self.palette.input_bg,
                    border_color: self.palette.border_fg,
                    form_id: fid,
                    text: value,
                    href: action_method,
                    extra: format!("{}\t{}\t{}", name, placeholder, input_type),
                },
            );
            return Some(id);
        }

        if tag == "textarea" {
            let name = el.attr("name").unwrap_or("").trim().to_string();
            let placeholder = el.attr("placeholder").unwrap_or("").trim().to_string();
            let value = collapse_whitespace(&el_ref.text().collect::<String>());
            let (fid, action_method) = match form_ctx {
                Some(fc) => (fc.form_id, format!("{}|{}", fc.action, fc.method)),
                None => (-1, format!("{}|GET", self.base_url)),
            };
            let box_w = self.viewport_content_w.max(140.0);
            let box_h = 74.0_f32;
            self.node_count += 1;
            let style = Style {
                size: Size {
                    width: length(box_w),
                    height: length(box_h),
                },
                max_size: Size {
                    width: percent(1.0_f32),
                    height: auto(),
                },
                margin: Rect {
                    left: length(0.0_f32),
                    right: length(0.0_f32),
                    top: length(4.0_f32),
                    bottom: length(6.0_f32),
                },
                ..Default::default()
            };
            let id = self
                .tree
                .new_leaf_with_context(
                    style,
                    NodeContext::Fixed {
                        width: box_w,
                        height: box_h,
                    },
                )
                .ok()?;
            self.metas.insert(
                id,
                RenderMeta {
                    kind: KIND_INPUT,
                    flags: FLAG_MULTILINE,
                    font_size: 14.0,
                    fg_color: self.palette.text_fg,
                    bg_color: self.palette.input_bg,
                    border_color: self.palette.border_fg,
                    form_id: fid,
                    text: value,
                    href: action_method,
                    extra: format!("{}\t{}\ttextarea", name, placeholder),
                },
            );
            return Some(id);
        }

        if tag == "select" {
            let name = el.attr("name").unwrap_or("").trim().to_string();
            let mut chosen_label = String::new();
            let mut chosen_val = String::new();
            let mut options_encoded: Vec<String> = Vec::new();
            if let Ok(opt_sel) = Selector::parse("option") {
                for opt in el_ref.select(&opt_sel) {
                    let lbl = sanitize_extra(&collapse_whitespace(&opt.text().collect::<String>()));
                    let val = sanitize_extra(opt.value().attr("value").unwrap_or(&lbl));
                    if options_encoded.len() < 64 {
                        options_encoded.push(format!("{}\x1e{}", val, lbl));
                    }
                    if chosen_label.is_empty() || opt.value().attr("selected").is_some() {
                        chosen_label = lbl;
                        chosen_val = val;
                    }
                }
            }
            let (fid, action_method) = match form_ctx {
                Some(fc) => (fc.form_id, format!("{}|{}", fc.action, fc.method)),
                None => (-1, format!("{}|GET", self.base_url)),
            };
            let box_w = self.viewport_content_w.min(220.0).max(110.0);
            let box_h = 36.0_f32;
            self.node_count += 1;
            let style = Style {
                size: Size {
                    width: length(box_w),
                    height: length(box_h),
                },
                flex_shrink: 1.0,
                margin: Rect {
                    left: length(0.0_f32),
                    right: length(4.0_f32),
                    top: length(4.0_f32),
                    bottom: length(6.0_f32),
                },
                ..Default::default()
            };
            let id = self
                .tree
                .new_leaf_with_context(
                    style,
                    NodeContext::Fixed {
                        width: box_w,
                        height: box_h,
                    },
                )
                .ok()?;
            let placeholder_lbl = if chosen_label.is_empty() {
                "Chọn giá trị".to_string()
            } else {
                chosen_label.clone()
            };
            self.metas.insert(
                id,
                RenderMeta {
                    kind: KIND_INPUT,
                    flags: 0,
                    font_size: 13.0,
                    fg_color: self.palette.text_fg,
                    bg_color: self.palette.surface_bg,
                    border_color: self.palette.border_fg,
                    form_id: fid,
                    text: if chosen_val.is_empty() { chosen_label } else { chosen_val },
                    href: action_method,
                    extra: format!(
                        "{}\t{}\tselect\t{}",
                        sanitize_extra(&name),
                        placeholder_lbl,
                        options_encoded.join("\x1f")
                    ),
                },
            );
            return Some(id);
        }

        if tag == "button" {
            let btn_type = el
                .attr("type")
                .unwrap_or("submit")
                .trim()
                .to_ascii_lowercase();
            let name = el.attr("name").unwrap_or("").trim().to_string();
            let val = el.attr("value").unwrap_or("").to_string();
            let mut label = collapse_whitespace(&el_ref.text().collect::<String>());
            if label.is_empty() {
                label = if val.is_empty() {
                    "Nút".to_string()
                } else {
                    val.clone()
                };
            }
            if !inherited_href.is_empty() && form_ctx.is_none() {
                return self.build_text_leaf(
                    label,
                    self.palette.link_fg,
                    font_size,
                    flags | FLAG_BOLD,
                    inherited_href,
                    false,
                );
            }
            let (fid, action_method) = match form_ctx {
                Some(fc) => (fc.form_id, format!("{}|{}", fc.action, fc.method)),
                None => (-1, format!("{}|GET", self.base_url)),
            };
            return self.build_button_leaf(
                label,
                fid,
                &action_method,
                &name,
                &val,
                &btn_type,
                css.bg_color.unwrap_or(self.palette.button_bg),
                css.color.unwrap_or(0xFFFF_FFFF),
            );
        }

        // Update form context if entering <form>
        let owned_form_ctx = if tag == "form" {
            let fid = self.next_form_id;
            self.next_form_id += 1;
            let raw_action = el.attr("action").unwrap_or("");
            let action = if raw_action.trim().is_empty() {
                self.base_url.to_string()
            } else {
                resolve_url(self.base_url, raw_action)
            };
            let method = if el
                .attr("method")
                .unwrap_or("get")
                .trim()
                .eq_ignore_ascii_case("post")
            {
                "POST".to_string()
            } else {
                "GET".to_string()
            };
            Some(FormContext {
                form_id: fid,
                action,
                method,
            })
        } else {
            None
        };
        let active_form = owned_form_ctx.as_ref().or(form_ctx);

        let resolved_href = if tag == "a" {
            el.attr("href")
                .map(|h| resolve_url(self.base_url, h))
                .unwrap_or_else(|| inherited_href.to_string())
        } else {
            inherited_href.to_string()
        };

        // Fast-path: pure inline leaf element (<a>, <b>, <span>, <code>, <i>...) with no block children
        let treat_as_inline_self = is_inline_tag(&tag) && css.display_flex.is_none();
        let mut child_ids = Vec::new();
        let mut inline_run = Vec::new();
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
                        (flags & FLAG_BOLD) != 0,
                    ) {
                        inline_run.push(tid);
                    }
                }
                Node::Element(child_el_val) => {
                    let child_tag = child_el_val.name().to_ascii_lowercase();
                    if child_tag == "br" {
                        self.flush_inline_run(&mut inline_run, &mut child_ids);
                        continue;
                    }
                    if let Some(child_el) = ElementRef::wrap(child) {
                        let pass_prefix = list_prefix || is_first_li_child;
                        let child_is_tr = tag == "tr";
                        if let Some(cid) = self.build_element(
                            child_el,
                            fg,
                            font_size,
                            flags,
                            &resolved_href,
                            pass_prefix,
                            active_form,
                            child_is_tr,
                            next_light_bg,
                        ) {
                            is_first_li_child = false;
                            if is_inline_tag(&child_tag) && !treat_as_inline_self {
                                inline_run.push(cid);
                            } else if treat_as_inline_self && is_inline_tag(&child_tag) {
                                inline_run.push(cid);
                            } else {
                                self.flush_inline_run(&mut inline_run, &mut child_ids);
                                child_ids.push(cid);
                            }
                        }
                    }
                }
                _ => {}
            }
        }
        self.flush_inline_run(&mut inline_run, &mut child_ids);

        if child_ids.is_empty() && css.bg_color.is_none() && css.height_px.is_none() {
            return None;
        }

        // If this is a simple inline wrapper with a single leaf child and no box styling, return the leaf directly
        if treat_as_inline_self
            && child_ids.len() == 1
            && css.bg_color.is_none()
            && css.border_color.is_none()
            && css.padding_left.is_none()
            && css.padding_top.is_none()
        {
            return Some(child_ids[0]);
        }

        let (display, flex_direction, flex_wrap) = if let Some(dir) = css.display_flex {
            (
                Display::Flex,
                dir,
                css.flex_wrap.unwrap_or(if dir == FlexDirection::Row {
                    FlexWrap::Wrap
                } else {
                    FlexWrap::NoWrap
                }),
            )
        } else if treat_as_inline_self {
            (Display::Flex, FlexDirection::Row, FlexWrap::Wrap)
        } else {
            (Display::Block, FlexDirection::Column, FlexWrap::NoWrap)
        };

        let gap_val = css.gap_px.unwrap_or(if treat_as_inline_self { 4.0 } else { 0.0 });
        let container_style = Style {
            display,
            flex_direction,
            flex_wrap,
            justify_content: css.justify_content,
            align_items: css.align_items,
            flex_grow: if in_table_row {
                1.0
            } else {
                css.flex_grow.unwrap_or(0.0)
            },
            flex_shrink: 1.0,
            gap: Size {
                width: length(gap_val),
                height: length(gap_val),
            },
            size: Size {
                width: css.width_px.map(length).unwrap_or(auto()),
                height: css.height_px.map(length).unwrap_or(auto()),
            },
            min_size: Size {
                width: css.min_width_px.map(length).unwrap_or(auto()),
                height: css.min_height_px.map(length).unwrap_or(auto()),
            },
            max_size: Size {
                width: css.max_width_px.map(length).unwrap_or(percent(1.0_f32)),
                height: auto(),
            },
            margin: Rect {
                left: length(css.margin_left.unwrap_or(0.0)),
                right: length(css.margin_right.unwrap_or(0.0)),
                top: length(css.margin_top.unwrap_or(0.0)),
                bottom: length(css.margin_bottom.unwrap_or(0.0)),
            },
            padding: Rect {
                left: length(css.padding_left.unwrap_or(0.0)),
                right: length(css.padding_right.unwrap_or(0.0)),
                top: length(css.padding_top.unwrap_or(0.0)),
                bottom: length(css.padding_bottom.unwrap_or(0.0)),
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
                    form_id: -1,
                    text: String::new(),
                    href: String::new(),
                    extra: String::new(),
                },
            );
        }
        Some(id)
    }

    fn build_button_leaf(
        &mut self,
        label: String,
        form_id: i32,
        action_method: &str,
        name: &str,
        val: &str,
        btn_type: &str,
        bg_color: u32,
        fg_color: u32,
    ) -> Option<NodeId> {
        self.node_count += 1;
        let char_count = label.chars().count().max(3) as f32;
        let w = (char_count * 8.5 + 24.0).clamp(68.0, self.viewport_content_w.max(100.0));
        let h = 36.0_f32;
        let style = Style {
            size: Size {
                width: length(w),
                height: length(h),
            },
            flex_shrink: 0.0,
            margin: Rect {
                left: length(0.0_f32),
                right: length(6.0_f32),
                top: length(4.0_f32),
                bottom: length(6.0_f32),
            },
            ..Default::default()
        };
        let id = self
            .tree
            .new_leaf_with_context(style, NodeContext::Fixed { width: w, height: h })
            .ok()?;
        self.metas.insert(
            id,
            RenderMeta {
                kind: KIND_BUTTON,
                flags: FLAG_BOLD | FLAG_CENTER,
                font_size: 14.0,
                fg_color,
                bg_color,
                border_color: self.palette.border_fg,
                form_id,
                text: label,
                href: action_method.to_string(),
                extra: format!("{}\t{}\t{}", name, val, btn_type),
            },
        );
        Some(id)
    }

    fn build_text_leaf(
        &mut self,
        text: String,
        fg: u32,
        font_size: f32,
        flags: u8,
        href: &str,
        bold: bool,
    ) -> Option<NodeId> {
        self.node_count += 1;
        let style = Style {
            flex_shrink: 1.0,
            max_size: Size {
                width: percent(1.0_f32),
                height: auto(),
            },
            margin: Rect {
                left: length(0.0_f32),
                right: length(0.0_f32),
                top: length(1.0_f32),
                bottom: length(2.0_f32),
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
                    bold,
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
                form_id: -1,
                text,
                href: href.to_string(),
                extra: String::new(),
            },
        );
        Some(id)
    }

    fn build_reader_tree(&mut self, document: &Html, title: &str) -> Option<NodeId> {
        let mut children = Vec::new();
        if let Some(h1_id) = self.build_text_leaf(
            title.to_string(),
            self.palette.link_fg,
            23.0,
            FLAG_BOLD,
            "",
            true,
        ) {
            children.push(h1_id);
        }

        // Divider under title
        if let Ok(hr) = self.tree.new_leaf_with_context(
            Style {
                size: Size {
                    width: percent(1.0_f32),
                    height: length(2.0_f32),
                },
                margin: Rect {
                    left: length(0.0_f32),
                    right: length(0.0_f32),
                    top: length(8.0_f32),
                    bottom: length(12.0_f32),
                },
                ..Default::default()
            },
            NodeContext::Divider,
        ) {
            self.metas.insert(
                hr,
                RenderMeta {
                    kind: KIND_DIVIDER,
                    flags: 0,
                    font_size: 15.0,
                    fg_color: self.palette.border_fg,
                    bg_color: self.palette.border_fg,
                    border_color: 0,
                    form_id: -1,
                    text: String::new(),
                    href: String::new(),
                    extra: String::new(),
                },
            );
            children.push(hr);
        }

        let scope_sel = Selector::parse("article, main, .content, .article-body, body").ok();
        let root_scope = scope_sel
            .as_ref()
            .and_then(|s| document.select(s).next())
            .unwrap_or_else(|| document.root_element());

        let block_sel = Selector::parse("h1, h2, h3, p, blockquote, pre, li").ok();
        if let Some(sel) = block_sel {
            for el in root_scope.select(&sel) {
                if self.node_count >= MAX_RENDER_NODES {
                    break;
                }
                let tag = el.value().name().to_ascii_lowercase();
                // Skip blocks inside nav/footer/header/aside
                if el.ancestors().any(|a| {
                    a.value()
                        .as_element()
                        .map(|ae| {
                            matches!(
                                ae.name().to_ascii_lowercase().as_str(),
                                "nav" | "footer" | "aside" | "form"
                            )
                        })
                        .unwrap_or(false)
                }) {
                    continue;
                }
                let text = collapse_whitespace(&el.text().collect::<String>());
                if text.is_empty() || (tag == "p" && text.chars().count() < 25) {
                    continue;
                }
                let (fs, flags, bold, prefix) = match tag.as_str() {
                    "h1" => (21.0, FLAG_BOLD, true, String::new()),
                    "h2" => (19.0, FLAG_BOLD, true, String::new()),
                    "h3" => (17.5, FLAG_BOLD, true, String::new()),
                    "blockquote" => (16.5, FLAG_ITALIC, false, "❝ ".to_string()),
                    "pre" => (13.5, 0, false, String::new()),
                    "li" => (16.5, 0, false, "• ".to_string()),
                    _ => (17.0, 0, false, String::new()),
                };
                let full_text = if prefix.is_empty() {
                    text
                } else {
                    format!("{}{}", prefix, text)
                };
                if let Some(tid) = self.build_text_leaf(
                    full_text,
                    self.palette.text_fg,
                    fs,
                    flags,
                    "",
                    bold,
                ) {
                    let _ = self.tree.set_style(
                        tid,
                        Style {
                            flex_shrink: 1.0,
                            max_size: Size {
                                width: percent(1.0_f32),
                                height: auto(),
                            },
                            margin: Rect {
                                left: length(0.0_f32),
                                right: length(0.0_f32),
                                top: length(4.0_f32),
                                bottom: length(10.0_f32),
                            },
                            ..Default::default()
                        },
                    );
                    children.push(tid);
                }
            }
        }

        if children.len() <= 2 {
            if let Some(empty_msg) = self.build_text_leaf(
                "Không tìm thấy bài viết dài phù hợp cho Chế độ Đọc Báo trên trang này.".to_string(),
                self.palette.muted_fg,
                15.0,
                FLAG_ITALIC,
                "",
                false,
            ) {
                children.push(empty_msg);
            }
        }

        self.tree
            .new_with_children(
                Style {
                    display: Display::Block,
                    padding: Rect {
                        left: length(6.0_f32),
                        right: length(6.0_f32),
                        top: length(4.0_f32),
                        bottom: length(12.0_f32),
                    },
                    ..Default::default()
                },
                &children,
            )
            .ok()
    }

    fn collect_commands(
        &self,
        node_id: NodeId,
        offset_x: f32,
        offset_y: f32,
        out: &mut Vec<DisplayCommand>,
    ) {
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
                    form_id: meta.form_id,
                    text: meta.text.clone(),
                    href: meta.href.clone(),
                    extra: meta.extra.clone(),
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

pub fn render_html_with_options(
    html: &str,
    base_url: &str,
    extra_css: &str,
    opts: RenderOptions,
) -> WsRenderBuffer {
    let width = opts.viewport_width.clamp(240.0, 1280.0);
    let content_max_w = (width - 24.0).max(200.0);
    let palette = ThemePalette::for_mode(opts.dark_mode);
    let document = Html::parse_document(html);
    let title = extract_title(&document, base_url);
    let sheet = StyleSheet::parse(&document, extra_css);

    let mut state = BuildState {
        tree: TaffyTree::new(),
        metas: HashMap::new(),
        hidden_commands: Vec::new(),
        sheet: &sheet,
        palette,
        base_url,
        viewport_content_w: content_max_w,
        dark_mode: opts.dark_mode,
        block_ads: opts.block_ads,
        block_images: opts.block_images,
        node_count: 0,
        next_form_id: 1,
    };

    let content_node = if opts.reader_mode {
        state.build_reader_tree(&document, &title)
    } else {
        state.build_element(
            document.root_element(),
            palette.text_fg,
            15.0,
            0,
            "",
            false,
            None,
            false,
            !opts.dark_mode,
        )
    };

    let root_style = Style {
        display: Display::Block,
        padding: Rect {
            left: length(12.0_f32),
            right: length(12.0_f32),
            top: length(10.0_f32),
            bottom: length(24.0_f32),
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
        |known_dimensions, available_space, _node_id, node_context, _style| match node_context {
            Some(NodeContext::Text {
                text,
                font_size,
                bold,
            }) => measure_wrapped_text(
                text.as_str(),
                *font_size,
                *bold,
                known_dimensions.width,
                available_space.width,
                content_max_w,
            ),
            Some(NodeContext::Fixed {
                width: fw,
                height: fh,
            }) => Size {
                width: known_dimensions.width.unwrap_or(*fw),
                height: known_dimensions.height.unwrap_or(*fh),
            },
            Some(NodeContext::Divider) => Size {
                width: known_dimensions.width.unwrap_or(content_max_w),
                height: known_dimensions.height.unwrap_or(2.0),
            },
            _ => Size::ZERO,
        },
    );

    let content_height = state
        .tree
        .layout(root_node)
        .map(|l| l.size.height)
        .unwrap_or(120.0)
        .max(120.0);

    let mut commands = Vec::new();
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
        form_id: -1,
        text: String::new(),
        href: String::new(),
        extra: String::new(),
    });
    state.collect_commands(root_node, 0.0, 0.0, &mut commands);
    commands.extend(state.hidden_commands);

    serialize_display_list(&title, width, content_height, &commands)
}

fn write_len_prefixed_str(bytes: &mut Vec<u8>, s: &str) {
    let raw = s.as_bytes();
    bytes.extend_from_slice(&(raw.len() as u32).to_le_bytes());
    bytes.extend_from_slice(raw);
}

fn serialize_display_list(
    title: &str,
    content_width: f32,
    content_height: f32,
    commands: &[DisplayCommand],
) -> WsRenderBuffer {
    let mut bytes = Vec::with_capacity(64 + commands.len() * 64);
    bytes.extend_from_slice(&RENDER_MAGIC_V2.to_le_bytes());
    bytes.extend_from_slice(&content_width.to_le_bytes());
    bytes.extend_from_slice(&content_height.to_le_bytes());

    write_len_prefixed_str(&mut bytes, title);

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
        bytes.extend_from_slice(&cmd.form_id.to_le_bytes());

        write_len_prefixed_str(&mut bytes, &cmd.text);
        write_len_prefixed_str(&mut bytes, &cmd.href);
        write_len_prefixed_str(&mut bytes, &cmd.extra);
    }

    WsRenderBuffer {
        bytes,
        js_scripts: 0,
        js_errors: 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn read_u32_le(buf: &[u8], off: &mut usize) -> u32 {
        let v = u32::from_le_bytes(buf[*off..*off + 4].try_into().unwrap());
        *off += 4;
        v
    }

    fn read_f32_le(buf: &[u8], off: &mut usize) -> f32 {
        let v = f32::from_le_bytes(buf[*off..*off + 4].try_into().unwrap());
        *off += 4;
        v
    }

    fn read_str(buf: &[u8], off: &mut usize) -> String {
        let len = read_u32_le(buf, off) as usize;
        let s = String::from_utf8(buf[*off..*off + len].to_vec()).unwrap();
        *off += len;
        s
    }

    #[test]
    fn test_css_media_and_rgba_parsing() {
        let html = Html::parse_document("<html></html>");
        let css = r#"
            /* comment */
            @media (max-width: 600px) {
                .card { background-color: rgba(255, 255, 255, 0.9); color: #111; }
            }
            .after { font-size: 18px; }
        "#;
        let sheet = StyleSheet::parse(&html, css);
        let card = sheet.by_class.get("card").expect("card rule");
        assert_eq!(card.color, Some(0xFF11_1111));
        assert!(card.bg_color.is_some());
        let after = sheet.by_class.get("after").expect("after rule after @media");
        assert_eq!(after.font_size, Some(18.0));
    }

    #[test]
    fn test_resolve_and_extract_stylesheets() {
        let html = r#"
            <html><head>
                <link rel="stylesheet" href="/css/app.css">
                <link rel="stylesheet" href="//cdn.example.com/theme.css">
                <link rel="icon" href="/favicon.ico">
            </head></html>
        "#;
        let urls = extract_stylesheet_urls(html, "https://example.com/news/1");
        assert_eq!(
            urls,
            vec![
                "https://example.com/css/app.css",
                "https://cdn.example.com/theme.css"
            ]
        );
    }

    #[test]
    fn test_wsr2_dark_mode_light_parent_and_forms() {
        let html = r#"
            <html><head><title>Search</title></head><body>
                <div style="background:#ffffff">
                    <p style="color:#111111">Visible Dark Text</p>
                </div>
                <form action="/search" method="get">
                    <input type="hidden" name="hl" value="vi">
                    <input type="search" name="q" placeholder="Find">
                    <button type="submit">Go</button>
                </form>
                <div class="adsbygoogle">Ad Banner</div>
            </body></html>
        "#;
        let buf = render_html_with_options(
            html,
            "https://example.com",
            "",
            RenderOptions {
                viewport_width: 380.0,
                dark_mode: true,
                block_ads: true,
                block_images: false,
                reader_mode: false,
            },
        );
        let b = &buf.bytes;
        let mut off = 0usize;
        assert_eq!(read_u32_le(b, &mut off), RENDER_MAGIC_V2);
        let _w = read_f32_le(b, &mut off);
        let _h = read_f32_le(b, &mut off);
        assert_eq!(read_str(b, &mut off), "Search");

        let count = read_u32_le(b, &mut off);
        let mut saw_dark_text = false;
        let mut saw_hidden = false;
        let mut saw_input = false;
        let mut saw_ad = false;
        for _ in 0..count {
            let kind = b[off];
            let _flags = b[off + 1];
            off += 2;
            off += 20; // x, y, w, h, font_size
            let fg = read_u32_le(b, &mut off);
            let _bg = read_u32_le(b, &mut off);
            let _border = read_u32_le(b, &mut off);
            let _form_id = read_u32_le(b, &mut off);
            let text = read_str(b, &mut off);
            let _href = read_str(b, &mut off);
            let extra = read_str(b, &mut off);

            if text == "Visible Dark Text" {
                saw_dark_text = true;
                assert_eq!(fg, 0xFF11_1111);
            }
            if kind == KIND_HIDDEN && extra.starts_with("hl\t") {
                saw_hidden = true;
            }
            if kind == KIND_INPUT && extra.starts_with("q\tFind") {
                saw_input = true;
            }
            if text.contains("Ad Banner") {
                saw_ad = true;
            }
        }
        assert!(saw_dark_text);
        assert!(saw_hidden);
        assert!(saw_input);
        assert!(!saw_ad);
    }

    #[test]
    fn test_complex_css_and_interactive_form_controls() {
        let html = r#"
            <html><head><style>
                .box > p.lead { color: #ff0000; }
                input[type="checkbox"] { margin-top: 2px; }
                .grid { display: grid; flex-grow: 1; min-width: 120px; }
            </style></head><body>
                <div class="box"><p class="lead">Direct Lead</p></div>
                <form action="/apply" method="post">
                    <input type="checkbox" name="agree" value="yes" checked>
                    <input type="radio" name="plan" value="pro" checked>
                    <input type="radio" name="plan" value="free">
                    <select name="country">
                        <option value="us">United States</option>
                        <option value="vn" selected>Vietnam</option>
                    </select>
                </form>
            </body></html>
        "#;
        let buf = render_html_with_options(
            html,
            "https://example.com",
            "",
            RenderOptions {
                viewport_width: 380.0,
                dark_mode: false,
                block_ads: false,
                block_images: false,
                reader_mode: false,
            },
        );
        let b = &buf.bytes;
        let mut off = 0usize;
        assert_eq!(read_u32_le(b, &mut off), RENDER_MAGIC_V2);
        let _w = read_f32_le(b, &mut off);
        let _h = read_f32_le(b, &mut off);
        let _title = read_str(b, &mut off);
        let count = read_u32_le(b, &mut off);

        let mut saw_lead = false;
        let mut saw_checked_cb = false;
        let mut saw_checked_radio = false;
        let mut saw_unchecked_radio = false;
        let mut saw_select = false;

        for _ in 0..count {
            let kind = b[off];
            let flags = b[off + 1];
            off += 2;
            off += 20;
            let fg = read_u32_le(b, &mut off);
            let _bg = read_u32_le(b, &mut off);
            let _border = read_u32_le(b, &mut off);
            let _form_id = read_u32_le(b, &mut off);
            let text = read_str(b, &mut off);
            let _href = read_str(b, &mut off);
            let extra = read_str(b, &mut off);

            if text == "Direct Lead" {
                saw_lead = true;
                assert_eq!(fg, 0xFFFF_0000);
            }
            if kind == KIND_INPUT && extra == "agree\tyes\tcheckbox" {
                assert_ne!(flags & FLAG_CHECKED, 0);
                saw_checked_cb = true;
            }
            if kind == KIND_INPUT && extra == "plan\tpro\tradio" {
                assert_ne!(flags & FLAG_CHECKED, 0);
                saw_checked_radio = true;
            }
            if kind == KIND_INPUT && extra == "plan\tfree\tradio" {
                assert_eq!(flags & FLAG_CHECKED, 0);
                saw_unchecked_radio = true;
            }
            if kind == KIND_INPUT && extra.starts_with("country\tVietnam\tselect\t") {
                assert_eq!(text, "vn");
                assert!(extra.contains("us\x1eUnited States\x1fvn\x1eVietnam"));
                saw_select = true;
            }
        }
        assert!(saw_lead);
        assert!(saw_checked_cb);
        assert!(saw_checked_radio);
        assert!(saw_unchecked_radio);
        assert!(saw_select);
    }
}
