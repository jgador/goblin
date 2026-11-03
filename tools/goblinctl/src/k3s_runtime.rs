//! Explicit application settings for deployment to the owned WSL cluster.
use crate::environment;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use serde_json::Value;
use serde_json::json;

#[derive(Default)]
pub struct Overrides {
    origin: Option<String>,
    chatgpt: Option<bool>,
}

pub struct Settings {
    pub origin: String,
    pub chatgpt: bool,
    pub environment: Value,
}

fn boolean(value: &str) -> Result<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "true" => Ok(true),
        "false" => Ok(false),
        _ => anyhow::bail!("GOBLIN_CHATGPT_ENABLED must be true or false"),
    }
}

impl Overrides {
    pub fn from_environment() -> Result<Self> {
        Self::parse(|name| match std::env::var(name) {
            Ok(value) => Ok(Some(value)),
            Err(std::env::VarError::NotPresent) => Ok(None),
            Err(_) => anyhow::bail!("{name} must be UTF-8"),
        })
    }

    pub(crate) fn parse(mut read: impl FnMut(&str) -> Result<Option<String>>) -> Result<Self> {
        Ok(Self {
            origin: read(environment::GOBLIN_PUBLIC_ORIGIN)?,
            chatgpt: read(environment::GOBLIN_CHATGPT_ENABLED)?
                .as_deref()
                .map(boolean)
                .transpose()?,
        })
    }

    pub fn resolve(&self, container: &Value, browser_origin: &str) -> Result<Settings> {
        let mut entries = container["env"]
            .as_array()
            .context("Missing platform environment")?
            .clone();
        let value = |name: &str| -> Result<Option<&str>> {
            let matching: Vec<_> = entries.iter().filter(|e| e["name"] == name).collect();
            ensure!(matching.len() <= 1, "Duplicate platform setting: {name}");
            matching
                .first()
                .map(|e| {
                    e["value"]
                        .as_str()
                        .context("Platform settings must use literal values")
                })
                .transpose()
        };
        let original_origin = self
            .origin
            .as_deref()
            .or(value(environment::GOBLIN_PUBLIC_ORIGIN)?)
            .unwrap_or(browser_origin);
        ensure!(
            !original_origin.contains(['#', '\\']),
            "Invalid GOBLIN_PUBLIC_ORIGIN"
        );
        let chatgpt = self
            .chatgpt
            .or(value(environment::GOBLIN_CHATGPT_ENABLED)?
                .map(boolean)
                .transpose()?)
            .unwrap_or(false);
        let uri: hyper::Uri = original_origin
            .parse()
            .context("Invalid GOBLIN_PUBLIC_ORIGIN")?;
        let mut origin = original_origin
            .strip_suffix('/')
            .unwrap_or(original_origin)
            .to_owned();
        ensure!(
            uri.authority().is_some_and(
                |a| !a.as_str().contains('@') && a.port().is_none_or(|p| p.as_u16() > 0)
            ) && uri.host().is_some_and(|h| !h.is_empty())
                && uri.path_and_query().is_none_or(|p| p.as_str() == "/")
                && (uri.scheme_str() == Some("https") || origin == browser_origin),
            "GOBLIN_PUBLIC_ORIGIN must be an HTTPS origin or the installed loopback browser origin"
        );
        ensure!(
            !chatgpt || uri.scheme_str() == Some("https"),
            "ChatGPT requires an HTTPS GOBLIN_PUBLIC_ORIGIN"
        );
        if uri.scheme_str() == Some("https") {
            let authority = uri.authority().context("Missing HTTPS authority")?;
            let host = uri.host().context("Missing HTTPS host")?;
            let suffix = authority
                .as_str()
                .strip_prefix(host)
                .context("Invalid HTTPS authority")?;
            ensure!(
                suffix.is_empty() || authority.port().is_some(),
                "Invalid HTTPS port"
            );
            let port = authority.port().map(|p| p.as_u16()).filter(|p| *p != 443);
            origin = format!(
                "https://{}{}",
                host.to_ascii_lowercase(),
                port.map(|p| format!(":{p}")).unwrap_or_default()
            );
        }
        ensure!(
            origin.len() + "/mcp".len() <= 512,
            "The OAuth origin is too long"
        );
        for (name, value) in [
            (environment::GOBLIN_PUBLIC_ORIGIN, origin.clone()),
            (environment::GOBLIN_CHATGPT_ENABLED, chatgpt.to_string()),
        ] {
            if let Some(entry) = entries.iter_mut().find(|e| e["name"] == name) {
                *entry = json!({"name":name,"value":value});
            } else {
                entries.push(json!({"name":name,"value":value}));
            }
        }
        Ok(Settings {
            origin,
            chatgpt,
            environment: Value::Array(entries),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overrides_preserve_unrelated_settings_and_unset_retains_installed_configuration() {
        let container = json!({"env":[
            {"name":environment::GOBLIN_PUBLIC_ORIGIN,"value":"https://app.example.com"},
            {"name":environment::GOBLIN_CHATGPT_ENABLED,"value":"true"},
            {"name":environment::GOBLIN_EXECUTION_IMAGE,"value":"localhost/goblin-platform:test"},
            {"name":"GOBLIN_NODE_NAME","valueFrom":{"fieldRef":{"fieldPath":"spec.nodeName"}}}
        ]});
        let settings = Overrides::default()
            .resolve(&container, "http://localhost:8788")
            .unwrap();
        assert_eq!(settings.origin, "https://app.example.com");
        assert!(settings.chatgpt);
        assert_eq!(settings.environment, container["env"]);
        let disabled = Overrides::parse(|name| {
            Ok((name == environment::GOBLIN_CHATGPT_ENABLED).then(|| "FALSE".to_owned()))
        })
        .unwrap();
        assert!(
            !disabled
                .resolve(&container, "http://localhost:8788")
                .unwrap()
                .chatgpt
        );
    }

    #[test]
    fn invalid_inputs_fail_before_deployment() {
        assert!(Overrides::parse(|_| Ok(Some("yes".to_owned()))).is_err());
        let container = json!({"env":[]});
        for origin in [
            "",
            "http://app.example.com",
            "https://app.example.com/path",
            "https://app.example.com?query",
            "https://user:password@app.example.com",
            "https://app.example.com#fragment",
            "https://app.example.com:0",
            "https://app.example.com//",
            "https://app.example.com:invalid",
        ] {
            assert!(
                Overrides {
                    origin: Some(origin.to_owned()),
                    chatgpt: Some(true)
                }
                .resolve(&container, "http://localhost:8788")
                .is_err(),
                "{origin}"
            );
        }
        assert!(
            Overrides {
                origin: None,
                chatgpt: Some(true)
            }
            .resolve(&container, "http://localhost:8788")
            .is_err()
        );
        assert!(Overrides::default().resolve(&json!({"env":[{"name":environment::GOBLIN_PUBLIC_ORIGIN,"value":"https://app.example.com"},{"name":environment::GOBLIN_PUBLIC_ORIGIN,"value":"https://other.example.com"}]}),"http://localhost:8788").is_err());
    }
    #[test]
    fn https_origin_is_canonical_for_oauth_discovery() {
        for (input, expected) in [
            ("https://APP.EXAMPLE.COM:443/", "https://app.example.com"),
            (
                "https://app.example.com:8443/",
                "https://app.example.com:8443",
            ),
            ("https://[::1]:8443", "https://[::1]:8443"),
        ] {
            let settings = Overrides {
                origin: Some(input.to_owned()),
                chatgpt: Some(true),
            }
            .resolve(&json!({"env":[]}), "http://localhost:8788")
            .unwrap();
            assert_eq!(settings.origin, expected);
        }
    }
}
