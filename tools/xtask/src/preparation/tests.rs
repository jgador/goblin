use super::*;
use serde_json::json;

fn fixture() -> (release::Inputs, release::Release, Inventory) {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let snapshot = release::inputs(root).unwrap();
    let mut published = dependencies::installer(root).unwrap();
    published.version = "0.1.4".into();
    published.installer = snapshot.clone();
    let inventory = Inventory {
        reserved: BTreeSet::from(["goblinctl-v0.1.4".into()]),
        published_installers: BTreeSet::from(["0.1.4".into()]),
    };
    (snapshot, published, inventory)
}

#[test]
fn versions_stay_on_the_selected_line_and_reserve_incomplete_tags() {
    let tags = BTreeSet::from([
        "goblin-v0.1.2".into(),
        "goblin-v0.1.3-preview.1".into(),
        "goblin-v2.5.0".into(),
        "goblinctl-v9.0.0".into(),
    ]);
    assert_eq!(
        version_for_line(&tags, "release/0.1", Channel::Preview, None).unwrap(),
        "0.1.3-preview.2"
    );
    assert_eq!(
        version_for_line(&tags, "release/0.1", Channel::Stable, None).unwrap(),
        "0.1.3"
    );
    assert_eq!(
        version_for_line(&tags, "release/3.7", Channel::Preview, None).unwrap(),
        "3.7.0-preview.1"
    );
    for version in ["0.1.2", "2.5.1", "0.1.3-preview.2"] {
        assert!(version_for_line(&tags, "release/0.1", Channel::Stable, Some(version)).is_err());
    }
    for branch in [
        "master",
        "release/01.1",
        "release/0.1/extra",
        "release/../master",
        "release/0.1-preview.1",
    ] {
        assert!(line(branch).is_err(), "{branch}");
    }
}

#[test]
fn unchanged_installer_is_reused_with_its_original_source_revision() {
    let (snapshot, published, inventory) = fixture();
    let selected = select_installer(
        &snapshot,
        &snapshot.capabilities,
        &published,
        "0.1.4",
        &inventory,
        None,
        |_| Ok(published.clone()),
    )
    .unwrap();
    assert!(matches!(selected, Selection::Reuse(actual) if actual == published));
}

#[test]
fn reused_installer_tag_must_match_its_authenticated_source() {
    let (_, mut published, _) = fixture();
    published.source_revision = "a".repeat(40);
    let tag = json!({"object":{"type":"commit","sha":published.source_revision}});
    assert_eq!(
        verify_reused_installer(&published, || Ok(published.clone()), || Ok(tag)).unwrap(),
        published
    );

    for invalid in [
        json!({"object":{"type":"commit","sha":"b".repeat(40)}}),
        json!({"object":{"type":"tag","sha":published.source_revision}}),
        json!({}),
    ] {
        let message = verify_reused_installer(&published, || Ok(published.clone()), || Ok(invalid))
            .unwrap_err()
            .to_string();
        assert!(message.contains(&published.version));
        assert!(message.contains(&published.source_revision));
        assert!(message.contains("--installer-version"));
        assert!(message.contains("keep existing tags and assets unchanged"));
    }
}

#[test]
fn selected_installer_verification_failures_never_fall_back_to_a_build() {
    let (_, published, _) = fixture();
    let failed = verify_reused_installer(
        &published,
        || anyhow::bail!("Attestation failed"),
        || panic!("Unauthenticated artifacts cannot reach the tag check"),
    )
    .unwrap_err();
    assert!(format!("{failed:#}").contains("Attestation failed"));
    assert!(failed.to_string().contains("--installer-version"));

    let mut changed = published.clone();
    changed.sha256 = "b".repeat(64);
    let failed = verify_reused_installer(
        &published,
        || Ok(changed),
        || panic!("Changed artifacts cannot reach the tag check"),
    )
    .unwrap_err();
    assert!(failed.to_string().contains("changed during preparation"));
}

#[test]
fn bundle_changes_additions_and_deletions_build_without_a_version_bump() {
    let (snapshot, published, inventory) = fixture();
    let bundle = snapshot
        .files
        .keys()
        .find(|path| path.starts_with("deploy/azure/setup/"))
        .unwrap();
    let mut modified = snapshot.clone();
    modified.files.insert(bundle.clone(), "a".repeat(64));
    let mut added = snapshot.clone();
    added
        .files
        .insert("deploy/azure/setup/new-asset.js".into(), "a".repeat(64));
    let mut removed = snapshot.clone();
    removed.files.remove(bundle);
    for changed in [modified, added, removed] {
        let selected = select_installer(
            &changed,
            &changed.capabilities,
            &published,
            "0.1.4",
            &inventory,
            None,
            |_| Ok(published.clone()),
        )
        .unwrap();
        assert!(matches!(selected, Selection::Build(version) if version == "0.1.5"));
    }
}

#[test]
fn later_matching_publication_is_reused_without_a_pin_update() {
    let (snapshot, mut pin, mut inventory) = fixture();
    let mut matching = pin.clone();
    matching.version = "0.1.5".into();
    pin.installer
        .files
        .insert("older-bundle".into(), "a".repeat(64));
    inventory
        .published_installers
        .insert(matching.version.clone());
    inventory.reserved.insert("goblinctl-v0.1.5".into());
    let selected = select_installer(
        &snapshot,
        &snapshot.capabilities,
        &pin,
        "0.1.4",
        &inventory,
        None,
        |version| {
            Ok(if version == pin.version {
                pin.clone()
            } else {
                matching.clone()
            })
        },
    )
    .unwrap();
    assert!(matches!(selected, Selection::Reuse(actual) if actual == matching));
}

#[test]
fn missing_capabilities_and_failed_metadata_lookup_stop_preparation() {
    let (snapshot, published, inventory) = fixture();
    let mut required = snapshot.capabilities.clone();
    required.insert("install.future.v1".into());
    let result = select_installer(
        &snapshot,
        &required,
        &published,
        "0.1.4",
        &inventory,
        None,
        |_| panic!("Must stop before inspecting publications"),
    );
    assert!(result.unwrap_err().to_string().contains("Backport"));
    let result = select_installer(
        &snapshot,
        &snapshot.capabilities,
        &published,
        "0.1.4",
        &inventory,
        None,
        |_| anyhow::bail!("Metadata lookup failed"),
    );
    assert!(
        result
            .unwrap_err()
            .to_string()
            .contains("Metadata lookup failed")
    );
}

#[test]
fn explicit_versions_cannot_replace_reserved_or_incompatible_installers() {
    let (mut snapshot, published, mut inventory) = fixture();
    inventory.reserved.insert("goblinctl-v0.1.5".into());
    snapshot.files.remove("LICENSE");
    for version in ["0.1.4", "0.1.5", "../escape", "0.1.6-preview.1"] {
        assert!(
            select_installer(
                &snapshot,
                &snapshot.capabilities,
                &published,
                "0.1.4",
                &inventory,
                Some(version),
                |_| Ok(published.clone())
            )
            .is_err(),
            "{version}"
        );
    }
    let selected = select_installer(
        &snapshot,
        &snapshot.capabilities,
        &published,
        "0.1.4",
        &inventory,
        Some("0.1.6"),
        |_| panic!("Unused version needs no publication lookup"),
    )
    .unwrap();
    assert!(matches!(selected, Selection::Build(version) if version == "0.1.6"));
    assert_eq!(installer_version(&inventory, "0.1.4").unwrap(), "0.1.6");
}

#[test]
fn only_latest_successful_actions_gate_for_the_exact_source_qualifies() {
    let source = "a".repeat(40);
    let success = json!({"id":10,"head_sha":source,"name":"goblin-checks","app":{"id":15368},"status":"completed","conclusion":"success"});
    assert_eq!(
        successful_check(&[json!({"check_runs":[success]})], &source).unwrap(),
        10
    );
    for (field, value) in [
        ("conclusion", json!("failure")),
        ("status", json!("in_progress")),
        ("head_sha", json!("b".repeat(40))),
        ("app", json!({"id":1})),
    ] {
        let mut invalid = success.clone();
        invalid[field] = value;
        assert!(successful_check(&[json!({"check_runs":[invalid]})], &source).is_err());
    }
    let mut later = success.clone();
    later["id"] = json!(11);
    later["conclusion"] = json!("failure");
    assert!(
        successful_check(
            &[
                json!({"check_runs":[success]}),
                json!({"check_runs":[later]})
            ],
            &source
        )
        .is_err()
    );
    assert!(successful_check(&[], &source).is_err());
}

#[test]
fn source_export_uses_the_frozen_commit_and_excludes_local_files() {
    let repository = tempfile::tempdir().unwrap();
    git(repository.path(), &["init", "--initial-branch=master"]).unwrap();
    fs::write(repository.path().join("bundle.txt"), "release-line").unwrap();
    git(repository.path(), &["add", "bundle.txt"]).unwrap();
    git(
        repository.path(),
        &[
            "-c",
            "user.name=Release Fixture",
            "-c",
            "user.email=release@example.invalid",
            "commit",
            "-m",
            "Fixture source",
        ],
    )
    .unwrap();
    let frozen = git(repository.path(), &["rev-parse", "HEAD"]).unwrap();
    fs::write(repository.path().join("bundle.txt"), "later-development").unwrap();
    fs::write(repository.path().join("untracked.txt"), "local-only").unwrap();
    let staging = tempfile::tempdir().unwrap();
    let source = staging.path().join("source");
    export_source(repository.path(), &frozen, &source).unwrap();
    assert_eq!(
        fs::read_to_string(source.join("bundle.txt")).unwrap(),
        "release-line"
    );
    assert!(!source.join("untracked.txt").exists());
}
