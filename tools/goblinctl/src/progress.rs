//! Public build progress is reconstructed from known BuildKit records. Compiler,
//! package-manager and command output stays exclusively in the private host log.
use crate::contract_values::SetupAction;
use crate::contract_values::SetupStep;
use crate::setup;
use anyhow::Result;
use std::io::BufRead;
use std::io::Write;
use std::path::Path;

pub fn build_output(path: &Path) -> Result<()> {
    let mut input = std::io::stdin().lock();
    let mut output = std::io::stdout().lock();
    let mut line = Vec::new();
    let mut oversized = false;
    loop {
        let bytes = input.fill_buf()?;
        if bytes.is_empty() {
            break;
        }
        let count = bytes
            .iter()
            .position(|b| *b == b'\n')
            .map_or(bytes.len(), |i| i + 1);
        output.write_all(&bytes[..count])?;
        output.flush()?;
        if line.len() + count <= 8192 && !oversized {
            line.extend_from_slice(&bytes[..count]);
        } else {
            oversized = true;
        }
        let complete = bytes[count - 1] == b'\n';
        input.consume(count);
        if complete {
            if !oversized
                && let Ok(line) = std::str::from_utf8(&line)
                && let Some(message) = build_message(line.trim_end())
            {
                setup::update_scoped(path, SetupAction::Detail, &message, Some(SetupStep::Image))?;
            }
            line.clear();
            oversized = false;
        }
    }
    Ok(())
}

fn build_message(line: &str) -> Option<String> {
    let (number, record) = line.strip_prefix('#')?.split_once(' ')?;
    let number: u32 = number.parse().ok()?;
    let message = if record == "CACHED" {
        "reused from build cache".to_owned()
    } else if let Some(seconds) = record
        .strip_prefix("DONE ")
        .and_then(|s| s.strip_suffix('s'))
    {
        let seconds: f64 = seconds.parse().ok()?;
        if !seconds.is_finite() || !(0.0..=86400.0).contains(&seconds) {
            return None;
        }
        format!("completed in {seconds:.1}s")
    } else if record == "DONE" {
        "completed".to_owned()
    } else if record.starts_with("ERROR:") {
        "failed; diagnostics are available in the private installation log".to_owned()
    } else if record == "exporting layers" {
        "exporting image layers".to_owned()
    } else if record.starts_with("[internal] load ") {
        "loading build inputs".to_owned()
    } else if record.starts_with("[auth] ") {
        "requesting registry access for a build image".to_owned()
    } else if record.starts_with("[assets ")
        || record.starts_with("[build ")
        || record.starts_with("[stage-2 ")
    {
        let (_, instruction) = record.split_once("] ")?;
        match instruction {
            "RUN npm ci --omit=dev" => "installing runtime dependencies",
            "RUN npm --prefix frontend ci" => "installing frontend dependencies",
            "RUN npm --prefix frontend run build" => "compiling frontend assets",
            "RUN node /tmp/install-gh.mjs" => "downloading GitHub CLI",
            s if s.starts_with("RUN dotnet publish backend/src/Goblin.Web/") => {
                "publishing the .NET application"
            }
            s if s.starts_with("RUN dotnet publish backend/tools/Goblin.Database/") => {
                "publishing the database migration tool"
            }
            s if s.starts_with("COPY ") => "copying build files",
            s if s.starts_with("FROM ") => "preparing a build image",
            s if s.starts_with("RUN apt-get update") => "installing runtime packages",
            _ => return None,
        }
        .to_owned()
    } else {
        return None;
    };
    Some(format!("Build step {number}: {message}."))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn build_records_never_copy_arbitrary_command_output_or_errors() {
        for input in [
            "#9 1.2 password=private",
            "#9 [assets 3/8] RUN echo private",
            "#9 DONE private",
            "#9 DONE NaNs",
            "private",
        ] {
            assert!(build_message(input).is_none());
        }
        for input in [
            "#9 ERROR: password=private",
            "#9 [build 4/5] RUN dotnet publish backend/src/Goblin.Web/private",
            "#9 [auth] private",
            "#9 [internal] load private",
        ] {
            let message = build_message(input).unwrap();
            assert!(!message.contains("password="));
            assert!(!message.contains("private") || message.contains("private installation log"));
        }
        assert_eq!(
            build_message("#2 [assets 3/8] RUN npm ci --omit=dev").unwrap(),
            "Build step 2: installing runtime dependencies."
        );
        assert_eq!(
            build_message("#3 [assets 4/8] RUN npm --prefix frontend ci").unwrap(),
            "Build step 3: installing frontend dependencies."
        );
        assert_eq!(
            build_message("#4 [assets 5/8] RUN npm --prefix frontend run build").unwrap(),
            "Build step 4: compiling frontend assets."
        );
        assert_eq!(
            build_message("#2 DONE 12.5s").unwrap(),
            "Build step 2: completed in 12.5s."
        );
        assert!(build_message("#2 CACHED").unwrap().contains("cache"));
    }
}
