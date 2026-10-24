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
    } else if record.starts_with('[') {
        let (stage, instruction) = record.strip_prefix('[')?.split_once("] ")?;
        if !matches!(
            stage.split_whitespace().next()?,
            "node-base"
                | "assets"
                | "codex"
                | "github-cli"
                | "dotnet-base"
                | "restore"
                | "build"
                | "publish"
                | "database"
                | "runtime"
        ) {
            return None;
        }
        // BuildKit includes cache mounts in RUN records. Recognize the command
        // without exposing mount options or arbitrary command output publicly.
        let instruction = if let Some(mut command) = instruction.strip_prefix("RUN ") {
            while command.starts_with("--mount=") {
                command = command.split_once(' ')?.1.trim_start();
            }
            command
        } else {
            instruction
        };
        match instruction {
            "npm ci --omit=dev --no-audit --no-fund" => "installing runtime dependencies",
            "npm --prefix frontend ci --no-audit --no-fund" => "installing frontend dependencies",
            "npm --prefix frontend run build" => "compiling frontend assets",
            "node /tmp/install-gh.mjs" => "downloading GitHub CLI",
            s if s.starts_with("dotnet restore backend/src/Goblin.Web/") => {
                "restoring .NET dependencies"
            }
            s if s.starts_with("dotnet publish backend/src/Goblin.Web/") => {
                "publishing the .NET application"
            }
            s if s.starts_with("dotnet build backend/src/Goblin.Web/") => {
                "compiling the .NET application"
            }
            s if s.starts_with("dotnet publish backend/tools/Goblin.Database/") => {
                "publishing the database migration tool"
            }
            s if s.starts_with("COPY ") => "copying build files",
            s if s.starts_with("FROM ") => "preparing a build image",
            s if s.starts_with("apt-get update") => "installing runtime packages",
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
            "#9 [codex 3/8] RUN --mount=type=cache,id=private echo private",
            "#9 [unknown 3/8] RUN node /tmp/install-gh.mjs",
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
            build_message("#2 [codex 2/3] RUN --mount=type=cache,id=goblin-platform-npm-codex,target=/root/.npm npm ci --omit=dev --no-audit --no-fund").unwrap(),
            "Build step 2: installing runtime dependencies."
        );
        assert_eq!(
            build_message("#3 [assets 2/4] RUN --mount=type=cache,id=goblin-platform-npm-frontend,target=/root/.npm npm --prefix frontend ci --no-audit --no-fund").unwrap(),
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

    #[test]
    fn independent_build_stages_report_safe_progress_with_cache_mounts() {
        for (record, expected) in [
            (
                "[restore 14/14] RUN --mount=type=cache,id=goblin-platform-nuget-http,target=/root/.local/share/NuGet/http-cache,sharing=locked     dotnet restore backend/src/Goblin.Web/Goblin.Web.csproj --nologo &&     dotnet restore backend/tools/Goblin.Database/Goblin.Database.csproj --nologo",
                "restoring .NET dependencies",
            ),
            (
                "[publish 2/2] RUN --mount=type=cache,id=goblin-platform-nuget,target=/root/.nuget/packages dotnet publish backend/src/Goblin.Web/Goblin.Web.csproj -c Release -o /publish --no-restore --nologo",
                "publishing the .NET application",
            ),
            (
                "[build 5/5] RUN --mount=type=cache,id=goblin-platform-nuget,target=/root/.nuget/packages dotnet build backend/src/Goblin.Web/Goblin.Web.csproj -c Release --no-restore --nologo",
                "compiling the .NET application",
            ),
            (
                "[database 2/2] RUN --mount=type=cache,id=goblin-platform-nuget,target=/root/.nuget/packages dotnet publish backend/tools/Goblin.Database/Goblin.Database.csproj -c Release -o /database --no-restore --nologo",
                "publishing the database migration tool",
            ),
            (
                "[runtime 2/13] RUN --mount=type=cache,id=goblin-platform-apt-lists,target=/var/lib/apt/lists,sharing=locked     apt-get update -o Acquire::Languages=none &&     apt-get install -y --no-install-recommends git ca-certificates",
                "installing runtime packages",
            ),
            (
                "[runtime 2/13] RUN --mount=type=cache,target=/var/lib/apt/lists --mount=type=cache,target=/var/cache/apt     apt-get update",
                "installing runtime packages",
            ),
            (
                "[github-cli 2/2] RUN node /tmp/install-gh.mjs",
                "downloading GitHub CLI",
            ),
        ] {
            assert_eq!(
                build_message(&format!("#5 {record}")).unwrap(),
                format!("Build step 5: {expected}.")
            );
        }
    }
}
