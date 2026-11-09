//! Engine limits shared by boot declarations and native RegisterProgram. Shape and names use the JSON schema.
use super::{Diagnostic, codes};
use super::json::Node;
use std::collections::HashSet;
pub(super) fn check(file: &str, root: &Node) -> Vec<Diagnostic> {
    let mut out=Vec::new();
    for program in root.get("programs").and_then(Node::as_object).into_iter().flatten() {
        let value=&program.value;
        let count=|names:&[&str]| names.iter().map(|name| value.get(name).and_then(Node::as_array).map_or(0,|a|a.len())).sum::<usize>();
        for (names,max) in [(&["tpl","tplOptional"][..],8),(&["read","readOptional","write","writeOptional"][..],42),
            (&["singletonsRead"][..],64),(&["singletonsWrite"][..],64),(&["otherTpl"][..],9),
            (&["otherRead","otherWrite"][..],92),(&["eventsIn","eventsOut"][..],24)] {
            if count(names)>max { out.push(Diagnostic::new(file,codes::SIZE,value.span,&format!("program '{}' exceeds engine limit {max} for {}",program.key,names.join(" + ")))); }
        }
        let system=value.get("flags").and_then(Node::as_array).is_none_or(|flags| flags.iter().any(|n|n.as_str()==Some("system")));
        let query=count(&["tpl","tplOptional","read","readOptional","write","writeOptional","with","without","anyOf"]);
        if if system {query>0} else {count(&["tpl","tplOptional","read","readOptional","write","writeOptional","with","anyOf"])==0} {
            out.push(Diagnostic::new(file,codes::SCHEMA_RULE,value.span,"system/query flag and entity access lists disagree"));
        }
        for names in [&["tpl","tplOptional"][..],&["read","readOptional","write","writeOptional"][..],&["with","without","anyOf"][..]] {
            let mut seen=HashSet::new();
            for name in names {
                for item in value.get(name).and_then(Node::as_array).into_iter().flatten() {
                    if let Some(component)=item.as_str() && !seen.insert(component) {
                        out.push(Diagnostic::new(file,codes::UNIQUE,item.span,"component appears twice in one engine access group"));
                    }
                }
            }
        }
    }
    out
}
#[cfg(test)] mod tests {
    use super::*;
    #[test] fn constraints_match_native_boot_parser() {
        let parsed=|text:&str| super::super::json::parse("kiln.programs.json",text,0,super::super::json::Dialect::STRICT).root.unwrap();
        assert!(check("p",&parsed(r#"{"format":1,"programs":{"a":{"otherRead":["Health"],"otherWrite":["Health"]}}}"#)).is_empty());
        assert!(!check("p",&parsed(r#"{"format":1,"programs":{"a":{"read":["Health"]}}}"#)).is_empty());
        assert!(!check("p",&parsed(r#"{"format":1,"programs":{"a":{"flags":[]}}}"#)).is_empty());
        assert!(!check("p",&parsed(r#"{"format":1,"programs":{"a":{"flags":["hostOnly"],"read":["Health"],"write":["Health"]}}}"#)).is_empty());
        let events=(0..13).map(|n|format!("Event{n}")).collect::<Vec<_>>();
        let json=serde_json::json!({"format":1,"programs":{"a":{"eventsIn":events,"eventsOut":events}}});
        assert!(check("p",&parsed(&json.to_string())).iter().any(|d|d.code==codes::SIZE));
    }
}
