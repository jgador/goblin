use super::*;
use serde_json::json;

fn save(directory: &Path, candidate: &Candidate) {
    files::write_json(
        &directory.join("release.json"),
        &serde_json::to_value(candidate).unwrap(),
        0o644,
    )
    .unwrap();
    fs::write(
        directory.join("SHA256SUMS"),
        checksums(directory, candidate).unwrap(),
    )
    .unwrap();
}

fn fixture() -> (tempfile::TempDir, Candidate) {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let scratch = root.join(".artifacts/candidate-tests");
    fs::create_dir_all(&scratch).unwrap();
    let directory = tempfile::tempdir_in(scratch).unwrap();
    let native = directory.path().join("installer");
    fs::create_dir(&native).unwrap();
    fs::write(
        native.join(release::ARCHIVE),
        "native archive identity fixture",
    )
    .unwrap();
    let mut installer = crate::dependencies::installer(root).unwrap();
    installer.source_revision = "a".repeat(40);
    installer.sha256 = install::checksum(&native.join(release::ARCHIVE)).unwrap();
    files::write_json(
        &native.join("release.json"),
        &serde_json::to_value(&installer).unwrap(),
        0o644,
    )
    .unwrap();
    fs::write(
        native.join("SHA256SUMS"),
        format!("{}  {}\n", installer.sha256, release::ARCHIVE),
    )
    .unwrap();
    let mut candidate = Candidate {
        schema_version: 2,
        version: "0.1.1-preview.2".into(),
        channel: Channel::Preview,
        source: Source {
            branch: "release/0.1".into(),
            revision: "a".repeat(40),
            branch_tip: "b".repeat(40),
            check_run_id: 42,
        },
        workflow_revision: "c".repeat(40),
        installer,
        installer_origin: InstallerOrigin::Built,
        changed_installer_inputs: vec![],
        run_id: "123".into(),
        run_attempt: "1".into(),
        assets: BTreeMap::new(),
        deployment_checks: Check::Pending,
    };
    for name in &azure::ASSETS[..2] {
        files::write_json(&directory.path().join(name), &json!({
            "parameters": { "goblinSourceRef": { "defaultValue":candidate.source.revision, "allowedValues":[candidate.source.revision] } },
            "metadata": { "goblin": {"version":candidate.version,"sourceRevision":candidate.source.revision,"installerVersion":candidate.installer.version,"installerSha256":candidate.installer.sha256} }
        }), 0o644).unwrap();
    }
    files::write_json(&directory.path().join(azure::ASSETS[2]), &json!({"parameters":{"basics":[{
        "name":"goblinSourceRef","defaultValue":candidate.version,"constraints":{"allowedValues":[{"label":candidate.version,"value":candidate.source.revision}]}
    }]}}), 0o644).unwrap();
    for name in azure::ASSETS.iter().map(|name| (*name).to_owned()).chain(
        [release::ARCHIVE, "release.json", "SHA256SUMS"].map(|name| format!("installer/{name}")),
    ) {
        candidate.assets.insert(
            name.clone(),
            install::checksum(&directory.path().join(name)).unwrap(),
        );
    }
    save(directory.path(), &candidate);
    (directory, candidate)
}

#[test]
fn pending_candidates_need_checks_and_the_original_run_identity_to_be_sealed() {
    let (directory, candidate) = fixture();
    validate(directory.path()).unwrap();
    assert!(verify(directory.path()).is_err());
    for (run, attempt, revision) in [
        ("999", "1", candidate.workflow_revision.as_str()),
        ("123", "2", candidate.workflow_revision.as_str()),
        ("123", "1", "wrong"),
    ] {
        assert!(seal(directory.path(), run, attempt, revision).is_err());
    }
    seal(directory.path(), "123", "1", &candidate.workflow_revision).unwrap();
    crate::goblin_release::verify(directory.path()).unwrap();
    assert!(seal(directory.path(), "123", "1", &candidate.workflow_revision).is_err());
}

#[test]
fn every_installer_and_azure_asset_is_bound_to_the_candidate() {
    let (directory, candidate) = fixture();
    for name in candidate.assets.keys() {
        let path = directory.path().join(name);
        let original = fs::read(&path).unwrap();
        fs::write(&path, "replacement").unwrap();
        assert!(validate(directory.path()).is_err(), "{name}");
        fs::write(&path, original).unwrap();
    }
    validate(directory.path()).unwrap();
}

#[test]
fn a_backport_can_reuse_an_older_installer_but_new_builds_must_use_its_source() {
    let (directory, mut candidate) = fixture();
    candidate.installer.source_revision = "d".repeat(40);
    files::write_json(
        &directory.path().join("installer/release.json"),
        &serde_json::to_value(&candidate.installer).unwrap(),
        0o644,
    )
    .unwrap();
    candidate.assets.insert(
        "installer/release.json".into(),
        install::checksum(&directory.path().join("installer/release.json")).unwrap(),
    );
    save(directory.path(), &candidate);
    assert!(validate(directory.path()).is_err());
    candidate.installer_origin = InstallerOrigin::Published;
    save(directory.path(), &candidate);
    validate(directory.path()).unwrap();
}

#[test]
fn a_rehashed_candidate_cannot_substitute_another_release_line_or_source() {
    let (directory, mut candidate) = fixture();
    candidate.source.branch = "release/0.2".into();
    save(directory.path(), &candidate);
    assert!(validate(directory.path()).is_err());
    candidate.source.branch = "release/0.1".into();
    let path = directory.path().join(azure::ASSETS[0]);
    let mut template = files::json(&path).unwrap();
    template["parameters"]["goblinSourceRef"]["defaultValue"] = json!("e".repeat(40));
    files::write_json(&path, &template, 0o644).unwrap();
    candidate
        .assets
        .insert(azure::ASSETS[0].into(), install::checksum(&path).unwrap());
    save(directory.path(), &candidate);
    assert!(validate(directory.path()).is_err());
}

#[test]
fn symlinks_and_extra_assets_are_rejected_before_reading_them() {
    let (directory, mut candidate) = fixture();
    candidate
        .assets
        .insert("../unexpected".into(), "a".repeat(64));
    save(directory.path(), &candidate);
    assert!(validate(directory.path()).is_err());
    candidate.assets.remove("../unexpected");
    let path = directory.path().join(azure::ASSETS[0]);
    fs::remove_file(&path).unwrap();
    std::os::unix::fs::symlink(azure::ASSETS[1], &path).unwrap();
    save(directory.path(), &candidate);
    assert!(validate(directory.path()).is_err());
}
