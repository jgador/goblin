//! Goblin's published installer selection and digest-pinned Docker images.
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Subcommand;
use goblinctl::files;
use regex::Regex;
use serde::Deserialize;
use serde::Serialize;
use serde_json::Value;
use sha2::Digest;
use sha2::Sha256;
use std::collections::BTreeMap;
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use toml_edit::DocumentMut;

const CATALOG: &str = "dependencies.toml";
const LOCK: &str = "dependencies.lock.json";
// Matches the platform used by the installer and its released native binary.
const PLATFORM: &str = "linux/amd64";

#[derive(Clone, Copy, PartialEq, Eq)]
enum Resolution {
    ReuseLocked,
    Refresh,
}

#[derive(Subcommand)]
pub enum Task {
    /// Resolve new/changed image tags and update image references in source files.
    Resolve {
        /// Explicitly resolve existing image tags again (including moving tags).
        #[arg(long)]
        refresh: bool,
    },
    /// Verify committed consumers and locks without downloads or changes.
    Check {
        #[arg(long, required = true)]
        locked: bool,
    },
    /// Show dependency declarations and resolved image references.
    List,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
struct Catalog {
    goblinctl: Release,
    images: BTreeMap<String, String>,
}
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
struct Release {
    version: String,
}
#[derive(Clone, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Lock {
    schema: u32,
    catalog_sha256: String,
    platform: String,
    images: BTreeMap<String, LockedImage>,
    goblinctl: Value,
}
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
struct LockedImage {
    reference: String,
    digest: String,
}

fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn valid_hash(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
}
fn read_catalog(root: &Path) -> Result<Catalog> {
    let catalog: Catalog = toml_edit::de::from_str(&fs::read_to_string(root.join(CATALOG))?)?;
    super::release::validate_version(&catalog.goblinctl.version)?;
    let image_reference = Regex::new(
        r"^[a-z0-9][a-z0-9._/-]*(?::[0-9]+/[a-z0-9._/-]+)?:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$",
    )?;
    for (id, reference) in &catalog.images {
        ensure!(
            !id.is_empty()
                && id
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b"_-".contains(&b)),
            "Invalid image name: {id}"
        );
        ensure!(
            image_reference.is_match(reference),
            "Image {id} must be repository:tag; digests belong in the generated lock"
        );
    }
    Ok(catalog)
}
fn read_lock(root: &Path) -> Result<Lock> {
    serde_json::from_slice(
        &fs::read(root.join(LOCK)).context("Run cargo xtask dependencies resolve")?,
    )
    .context("Invalid dependency lock")
}
pub fn installer(root: &Path) -> Result<super::release::Release> {
    let lock = read_lock(root)?;
    ensure!(lock.schema == 2, "Unsupported dependency lock schema");
    ensure!(
        lock.goblinctl["repository"] == super::release::GITHUB_REPOSITORY,
        "Invalid installer repository"
    );
    let release = serde_json::from_value(lock.goblinctl["release"].clone())?;
    super::release::validate(&release)?;
    Ok(release)
}
fn verify_lock(_root: &Path, catalog: &Catalog, lock: &Lock) -> Result<()> {
    ensure!(
        lock.schema == 2 && lock.catalog_sha256 == digest(&serde_json::to_vec(catalog)?),
        "Dependency catalog changed; run cargo xtask dependencies resolve"
    );
    ensure!(
        lock.platform == PLATFORM,
        "Dependency lock must target {PLATFORM}; run cargo xtask dependencies resolve"
    );
    ensure!(
        lock.goblinctl["repository"] == super::release::GITHUB_REPOSITORY
            && lock.goblinctl["release"]["version"] == catalog.goblinctl.version,
        "Installer selection differs from lock; use cargo xtask release pin-installer"
    );
    super::release::validate(&serde_json::from_value(lock.goblinctl["release"].clone())?)?;
    ensure!(
        lock.images.len() == catalog.images.len(),
        "Image lock is incomplete"
    );
    for (id, image) in &catalog.images {
        let locked = lock
            .images
            .get(id)
            .with_context(|| format!("Missing locked image: {id}"))?;
        ensure!(locked.reference == *image, "Image reference changed: {id}");
        ensure!(
            locked
                .digest
                .strip_prefix("sha256:")
                .is_some_and(valid_hash),
            "Invalid image digest: {id}"
        );
    }
    Ok(())
}

pub fn execute(root: &Path, task: Task) -> Result<()> {
    match task {
        Task::Resolve { refresh } => resolve(
            root,
            if refresh {
                Resolution::Refresh
            } else {
                Resolution::ReuseLocked
            },
        ),
        Task::Check { locked: _ } => check(root),
        Task::List => {
            let catalog = read_catalog(root)?;
            let lock = read_lock(root)?;
            verify_lock(root, &catalog, &lock)?;
            println!(
                "goblinctl {} ({})",
                catalog.goblinctl.version,
                super::release::GITHUB_REPOSITORY
            );
            for (name, image) in &lock.images {
                println!("image/{name} {}@{}", image.reference, image.digest);
            }
            Ok(())
        }
    }
}

fn inspect_image(reference: &str) -> Result<LockedImage> {
    eprintln!("Resolving image {reference}");
    let raw = files::output(Command::new("docker").args([
        "buildx",
        "imagetools",
        "inspect",
        reference,
        "--format",
        "{{json .Manifest}}",
    ]))?;
    let manifest: Value = serde_json::from_str(&raw)?;
    if manifest.get("manifests").is_some() {
        parse_image(reference, &manifest)
    } else {
        let digest = manifest["digest"]
            .as_str()
            .context("Registry did not return a digest")?;
        let immutable = format!("{reference}@{digest}");
        let config = files::output(Command::new("docker").args([
            "buildx",
            "imagetools",
            "inspect",
            &immutable,
            "--format",
            "{{json .Image}}",
        ]))?;
        parse_single_image(reference, digest, &serde_json::from_str(&config)?)
    }
}
fn parse_single_image(reference: &str, digest: &str, config: &Value) -> Result<LockedImage> {
    let os = config["os"]
        .as_str()
        .context("Image configuration has no OS")?;
    let architecture = config["architecture"]
        .as_str()
        .context("Image configuration has no architecture")?;
    ensure!(
        format!("{os}/{architecture}") == PLATFORM,
        "Image {reference} does not support {PLATFORM}"
    );
    Ok(LockedImage {
        reference: reference.into(),
        digest: digest.into(),
    })
}
fn parse_image(reference: &str, value: &Value) -> Result<LockedImage> {
    let digest = value["digest"]
        .as_str()
        .context("Registry did not return a digest")?
        .to_owned();
    // Fail closed for images without platform metadata; do not guess architecture.
    let supported = value["manifests"]
        .as_array()
        .context("Image must provide a platform index")?
        .iter()
        .any(|manifest| {
            let os = manifest["platform"]["os"].as_str().unwrap_or("unknown");
            let arch = manifest["platform"]["architecture"]
                .as_str()
                .unwrap_or("unknown");
            format!("{os}/{arch}") == PLATFORM
        });
    ensure!(supported, "Image {reference} does not support {PLATFORM}");
    Ok(LockedImage {
        reference: reference.into(),
        digest,
    })
}
fn resolve(root: &Path, resolution: Resolution) -> Result<()> {
    let catalog = read_catalog(root)?;
    let previous = if root.join(LOCK).exists() {
        read_lock(root)?
    } else {
        Lock::default()
    };
    let mut lock = Lock {
        schema: 2,
        catalog_sha256: digest(&serde_json::to_vec(&catalog)?),
        platform: PLATFORM.into(),
        goblinctl: previous.goblinctl.clone(),
        ..Lock::default()
    };
    for (id, image) in &catalog.images {
        let locked = match previous.images.get(id) {
            Some(locked)
                if resolution == Resolution::ReuseLocked
                    && previous.platform == PLATFORM
                    && locked.reference == *image =>
            {
                locked.clone()
            }
            _ => inspect_image(image)?,
        };
        lock.images.insert(id.clone(), locked);
    }
    verify_lock(root, &catalog, &lock)?;
    let updates = outputs(root, &previous, &lock)?;
    write_outputs(root, &updates)?;
    fs::write(root.join(LOCK), json_bytes(&lock)?)?;
    check(root)
}
fn json_bytes(value: &impl Serialize) -> Result<Vec<u8>> {
    let mut bytes = serde_json::to_vec_pretty(value)?;
    bytes.push(b'\n');
    Ok(bytes)
}
fn write_outputs(root: &Path, generated: &BTreeMap<String, Vec<u8>>) -> Result<()> {
    for (name, bytes) in generated {
        let path = root.join(name);
        fs::create_dir_all(path.parent().context("Output has no parent")?)?;
        fs::write(path, bytes)?;
    }
    Ok(())
}

pub fn check(root: &Path) -> Result<()> {
    let catalog = read_catalog(root)?;
    let lock = read_lock(root)?;
    verify_lock(root, &catalog, &lock)?;
    let expected = outputs(root, &lock, &lock)?;
    for (name, bytes) in &expected {
        ensure!(
            fs::read(root.join(name)).ok().as_ref() == Some(bytes),
            "Dependency reference or installation request is stale: {name}; run cargo xtask dependencies resolve"
        );
    }
    println!("Dependency catalog, locked images, and installation request agree");
    Ok(())
}

fn walk(directory: &Path) -> Result<Vec<PathBuf>> {
    let mut paths = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        ensure!(
            !entry.file_type()?.is_symlink(),
            "Dependency input cannot be a symlink: {}",
            entry.path().display()
        );
        if entry.file_type()?.is_dir() {
            if !matches!(
                entry.file_name().to_str(),
                Some("bin" | "obj" | "node_modules" | "target" | ".git")
            ) {
                paths.extend(walk(&entry.path())?);
            }
        } else {
            paths.push(entry.path());
        }
    }
    paths.sort();
    Ok(paths)
}
fn image_repository(reference: &str) -> &str {
    let image = reference.split('@').next().unwrap_or(reference);
    match image.rsplit_once(':') {
        Some((repository, tag)) if !tag.contains('/') => repository,
        _ => image,
    }
}

fn image_replacements(previous: &Lock, lock: &Lock) -> Result<BTreeMap<String, String>> {
    let mut images = BTreeMap::new();
    for (name, image) in &lock.images {
        let pinned = format!("{}@{}", image.reference, image.digest);
        // Keep the alias stable when moving an image to a different repository.
        for source in [Some(image), previous.images.get(name)]
            .into_iter()
            .flatten()
        {
            let repository = image_repository(&source.reference);
            if let Some(existing) = images.insert(repository.to_owned(), pinned.clone()) {
                ensure!(
                    existing == pinned,
                    "Ambiguous image repository: {repository}"
                );
            }
        }
    }
    Ok(images)
}

fn update_image_references(
    path: &str,
    source: &str,
    images: &BTreeMap<String, String>,
    image_line: &Regex,
) -> Result<String> {
    let mut output = String::new();
    let mut end = 0;
    for captures in image_line.captures_iter(source) {
        let image = captures.name("image").context("Missing image reference")?;
        // Installation replaces this locally built image with its source-derived tag.
        if path == "deploy/auth/sandbox.yaml" && image.as_str() == "goblin-auth:0.1.0" {
            continue;
        }
        let replacement = images.get(image_repository(image.as_str()));
        // Base images belong to their Dockerfile unless the catalog overrides them.
        if replacement.is_none() && matches!(path, "Dockerfile" | "scripts/check-logging.sh") {
            continue;
        }
        let pinned = replacement.with_context(|| {
            format!(
                "Image {} in {path} is missing from dependencies.toml",
                image.as_str()
            )
        })?;
        // Keep tags as tags; only YAML references already using a digest stay pinned.
        let reference = if matches!(path, "Dockerfile" | "scripts/check-logging.sh")
            || !image.as_str().contains('@')
        {
            pinned
                .split_once('@')
                .map_or(pinned.as_str(), |(tag, _)| tag)
        } else {
            pinned.as_str()
        };
        output.push_str(&source[end..image.start()]);
        output.push_str(reference);
        end = image.end();
    }
    output.push_str(&source[end..]);
    Ok(output)
}

fn outputs(root: &Path, previous: &Lock, lock: &Lock) -> Result<BTreeMap<String, Vec<u8>>> {
    let mut outputs = BTreeMap::new();
    let images = image_replacements(previous, lock)?;
    let docker_image = Regex::new(r"(?m)^FROM[\t ]+(?:--platform=\S+[\t ]+)?(?P<image>\S+)")?;
    let yaml_image = Regex::new(r#"(?m)^[\t ]*(?:-[\t ]+)?image:[\t ]+["']?(?P<image>[^\s"'#]+)"#)?;
    let mut paths = vec![
        root.join("Dockerfile"),
        root.join("scripts/check-logging.sh"),
    ];
    paths.extend(
        walk(&root.join("deploy"))?
            .into_iter()
            .filter(|path| path.extension().is_some_and(|v| v == "yaml" || v == "yml")),
    );
    for path in paths {
        let relative = path
            .strip_prefix(root)?
            .to_str()
            .context("Non-UTF8 image source path")?;
        let source = fs::read_to_string(&path)?;
        let image_line = if relative == "Dockerfile" || relative.ends_with(".sh") {
            &docker_image
        } else {
            &yaml_image
        };
        if image_line.is_match(&source) {
            outputs.insert(
                relative.into(),
                update_image_references(relative, &source, &images, image_line)?.into_bytes(),
            );
        }
    }
    let mut resources = Vec::new();
    for directory in ["deploy/auth", "deploy/azure/app"] {
        for path in walk(&root.join(directory))? {
            if path.extension().is_some_and(|v| v == "yaml" || v == "conf") {
                let name = path
                    .strip_prefix(root)?
                    .to_str()
                    .context("Non-UTF8 resource path")?
                    .to_owned();
                let bytes = match outputs.get(&name) {
                    Some(bytes) => bytes.clone(),
                    None => fs::read(&path)?,
                };
                resources.push(goblinctl::deployment::Resource {
                    path: name,
                    sha256: digest(&bytes),
                });
            }
        }
    }
    outputs.insert(
        "deploy/install-request.json".into(),
        json_bytes(&goblinctl::deployment::InstallRequest {
            format: goblinctl::deployment::Format::V1,
            resources,
        })?,
    );
    Ok(outputs)
}
/// Pin automation updates only release-derived data; it never refreshes image tags.
pub fn pin_release(root: &Path, release: &super::release::Release) -> Result<()> {
    let mut document: DocumentMut = fs::read_to_string(root.join(CATALOG))?.parse()?;
    document["goblinctl"]["version"] = toml_edit::value(&release.version);
    fs::write(root.join(CATALOG), document.to_string())?;
    let catalog = read_catalog(root)?;
    let mut lock = read_lock(root)?;
    lock.catalog_sha256 = digest(&serde_json::to_vec(&catalog)?);
    lock.goblinctl =
        serde_json::json!({"repository": super::release::GITHUB_REPOSITORY, "release": release});
    verify_lock(root, &catalog, &lock)?;
    fs::write(root.join(LOCK), json_bytes(&lock)?)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> tempfile::TempDir {
        let source = Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .parent()
            .unwrap();
        let temp = tempfile::tempdir().unwrap();
        let mut paths = Vec::new();
        for name in ["deploy/auth", "deploy/postgres", "deploy/azure/app"] {
            paths.extend(walk(&source.join(name)).unwrap());
        }
        for name in [CATALOG, LOCK, "Dockerfile", "scripts/check-logging.sh"] {
            paths.push(source.join(name));
        }
        for path in paths {
            let destination = temp.path().join(path.strip_prefix(source).unwrap());
            fs::create_dir_all(destination.parent().unwrap()).unwrap();
            fs::copy(path, destination).unwrap();
        }
        temp
    }

    #[test]
    fn new_resources_are_discovered_and_application_images_do_not_change_installer_inputs() {
        let temp = fixture();
        let root = temp.path();
        let mut lock = read_lock(root).unwrap();
        let before = outputs(root, &lock, &lock).unwrap();
        fs::write(root.join("deploy/auth/new.yaml"), "kind: ConfigMap\n").unwrap();
        lock.images.get_mut("headlamp").unwrap().reference =
            "ghcr.io/headlamp-k8s/headlamp:new-version".into();
        let after = outputs(root, &lock, &lock).unwrap();
        assert_eq!(
            before["deploy/postgres/postgres.yaml"],
            after["deploy/postgres/postgres.yaml"]
        );
        assert_ne!(
            before["deploy/auth/headlamp.yaml"],
            after["deploy/auth/headlamp.yaml"]
        );
        let request: goblinctl::deployment::InstallRequest =
            serde_json::from_slice(&after["deploy/install-request.json"]).unwrap();
        assert!(
            request
                .resources
                .iter()
                .any(|r| r.path == "deploy/auth/new.yaml")
        );
    }

    #[test]
    fn check_rejects_stale_and_unknown_images_without_repairing_them() {
        let temp = fixture();
        let root = temp.path();
        let lock = read_lock(root).unwrap();
        write_outputs(root, &outputs(root, &lock, &lock).unwrap()).unwrap();
        let path = root.join("deploy/postgres/postgres.yaml");
        for source in ["image: postgres:outdated\n", "image: untracked:latest\n"] {
            fs::write(&path, source).unwrap();
            assert!(
                check(root)
                    .unwrap_err()
                    .to_string()
                    .contains("postgres.yaml")
            );
            assert_eq!(fs::read_to_string(&path).unwrap(), source);
        }
    }

    #[test]
    fn catalog_changes_and_unsupported_platforms_invalidate_the_lock() {
        let temp = fixture();
        let root = temp.path();
        let mut catalog = read_catalog(root).unwrap();
        let mut lock = read_lock(root).unwrap();
        verify_lock(root, &catalog, &lock).unwrap();
        lock.platform = "linux/arm64".into();
        assert!(verify_lock(root, &catalog, &lock).is_err());
        let lock = read_lock(root).unwrap();
        catalog
            .images
            .insert("postgres".into(), "postgres:99".into());
        assert!(verify_lock(root, &catalog, &lock).is_err());
    }

    #[test]
    fn catalog_rejects_unknown_sections_and_invalid_image_references() {
        let temp = fixture();
        let path = temp.path().join(CATALOG);
        let content = fs::read_to_string(&path).unwrap();
        let postgres = read_catalog(temp.path()).unwrap().images["postgres"].clone();
        fs::write(&path, format!("{content}\n[packages]\nunknown = true\n")).unwrap();
        assert!(read_catalog(temp.path()).is_err());
        for reference in ["postgres", "postgres@sha256:abc", "postgres:16 --flag"] {
            fs::write(&path, content.replace(&postgres, reference)).unwrap();
            assert!(read_catalog(temp.path()).is_err());
        }
        fs::write(
            &path,
            content.replace(&postgres, "localhost:5000/postgres:16"),
        )
        .unwrap();
        read_catalog(temp.path()).unwrap();
    }

    #[test]
    fn resolution_reuses_image_locks_without_native_package_manifests_or_tool_setup() {
        let temp = fixture();
        let root = temp.path();
        let before = read_lock(root).unwrap();
        resolve(root, Resolution::ReuseLocked).unwrap();
        assert_eq!(before, read_lock(root).unwrap());
        assert!(!root.join("Cargo.toml").exists());
        assert!(!root.join("package.json").exists());
        assert!(!root.join("backend").exists());
    }

    #[test]
    fn release_pinning_updates_the_catalog_and_lock_without_refreshing_images() {
        let temp = fixture();
        let root = temp.path();
        let before = read_lock(root).unwrap();
        let mut release = installer(root).unwrap();
        release.version = "0.1.2".into();
        pin_release(root, &release).unwrap();
        let catalog = read_catalog(root).unwrap();
        let lock = read_lock(root).unwrap();
        assert_eq!(catalog.goblinctl.version, "0.1.2");
        assert_eq!(lock.goblinctl["release"]["version"], "0.1.2");
        assert_eq!(before.images, lock.images);
        verify_lock(root, &catalog, &lock).unwrap();
    }

    #[test]
    fn resolution_preserves_source_edits_and_updates_only_image_references() {
        let temp = fixture();
        let root = temp.path();
        let lock = read_lock(root).unwrap();
        let image = &lock.images["postgres"];
        let reference = &image.reference;
        let path = root.join("deploy/postgres/postgres.yaml");
        let source = fs::read_to_string(&path).unwrap();
        let edited = format!(
            "# Local workload comment\n{}\n# Keep this note\n",
            source.replace(reference, "postgres:outdated")
        );
        fs::write(&path, &edited).unwrap();
        resolve(root, Resolution::ReuseLocked).unwrap();
        assert_eq!(
            fs::read_to_string(&path).unwrap(),
            edited.replace("postgres:outdated", reference)
        );
    }

    #[test]
    fn build_images_keep_their_own_defaults_unless_explicitly_overridden() {
        let temp = fixture();
        let root = temp.path();
        let source = "# Build owns these defaults\nFROM node:24 AS assets\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:10.0\nLABEL example=preserved\n";
        fs::write(root.join("Dockerfile"), source).unwrap();
        resolve(root, Resolution::ReuseLocked).unwrap();
        assert_eq!(fs::read_to_string(root.join("Dockerfile")).unwrap(), source);
        let mut lock = read_lock(root).unwrap();
        for name in ["node", "dotnet-sdk", "dotnet-runtime"] {
            assert!(!lock.images.contains_key(name));
        }

        let reference = "mcr.microsoft.com/dotnet/aspnet:10.0.1-noble";
        let digest_value = format!("sha256:{}", "a".repeat(64));
        let mut document: DocumentMut = fs::read_to_string(root.join(CATALOG))
            .unwrap()
            .parse()
            .unwrap();
        document["images"]["dotnet-runtime"] = toml_edit::value(reference);
        fs::write(root.join(CATALOG), document.to_string()).unwrap();
        lock.images.insert(
            "dotnet-runtime".into(),
            LockedImage {
                reference: reference.into(),
                digest: digest_value,
            },
        );
        lock.catalog_sha256 = digest(&serde_json::to_vec(&read_catalog(root).unwrap()).unwrap());
        fs::write(root.join(LOCK), json_bytes(&lock).unwrap()).unwrap();
        assert!(check(root).is_err());
        resolve(root, Resolution::ReuseLocked).unwrap();
        assert_eq!(
            fs::read_to_string(root.join("Dockerfile")).unwrap(),
            source.replace("mcr.microsoft.com/dotnet/aspnet:10.0", reference)
        );
        assert!(
            fs::read_to_string(root.join("scripts/check-logging.sh"))
                .unwrap()
                .contains(&format!("FROM {reference}\n"))
        );
    }

    #[test]
    fn repository_changes_follow_the_existing_alias_and_preserve_yaml_formatting() {
        let temp = fixture();
        let root = temp.path();
        let previous = read_lock(root).unwrap();
        let mut lock = previous.clone();
        let image = lock.images.get_mut("postgres").unwrap();
        image.reference = "localhost:5000/postgres:17".into();
        let reference = image.reference.clone();
        let pinned = format!("{}@{}", image.reference, image.digest);
        fs::write(
            root.join("deploy/postgres/new.yaml"),
            "# Another workload\n  - image: 'postgres:old' # Keep this comment\n    args: [example]\n  - image: postgres:old@sha256:outdated\n",
        )
        .unwrap();
        let updated = outputs(root, &previous, &lock).unwrap();
        assert_eq!(
            String::from_utf8(updated["deploy/postgres/new.yaml"].clone()).unwrap(),
            format!(
                "# Another workload\n  - image: '{reference}' # Keep this comment\n    args: [example]\n  - image: {pinned}\n"
            )
        );
        assert!(
            String::from_utf8(updated["deploy/postgres/postgres.yaml"].clone())
                .unwrap()
                .contains(&format!("image: {reference}\n"))
        );
        // Once the lock is saved, references containing a registry port still match.
        write_outputs(root, &updated).unwrap();
        assert_eq!(updated, outputs(root, &lock, &lock).unwrap());
    }

    #[test]
    fn new_workloads_must_use_a_catalog_image() {
        let temp = fixture();
        let root = temp.path();
        let lock = read_lock(root).unwrap();
        fs::write(root.join("deploy/auth/new.yaml"), "image: untracked:1\n").unwrap();
        let error = outputs(root, &lock, &lock).unwrap_err().to_string();
        assert!(error.contains("deploy/auth/new.yaml"));
        assert!(error.contains("dependencies.toml"));
    }

    #[test]
    fn platform_metadata_is_required_and_attestations_are_not_platforms() {
        let value = serde_json::json!({"digest": "sha256:abc", "manifests": [
            {"digest": "sha256:def", "platform": {"os": "linux", "architecture": "amd64"}},
            {"digest": "sha256:456", "platform": {"os": "linux", "architecture": "arm64"}},
            {"digest": "sha256:123", "platform": {"os": "unknown", "architecture": "unknown"}}
        ]});
        let image = parse_image("example:1", &value).unwrap();
        assert_eq!(image.digest, "sha256:abc");
        let mut unsupported = value;
        unsupported["manifests"].as_array_mut().unwrap().remove(0);
        assert!(parse_image("example:1", &unsupported).is_err());
        assert!(parse_image("example:1", &serde_json::json!({"digest":"sha256:abc"})).is_err());
        let single = parse_single_image(
            "example:1",
            "sha256:abc",
            &serde_json::json!({"os": "linux", "architecture": "amd64"}),
        )
        .unwrap();
        assert_eq!(single.digest, "sha256:abc");
        assert!(parse_single_image("example:1", "sha256:abc", &serde_json::json!({})).is_err());
        assert!(
            parse_single_image(
                "example:1",
                "sha256:abc",
                &serde_json::json!({"os": "linux", "architecture": "arm64"})
            )
            .is_err()
        );
    }
}
