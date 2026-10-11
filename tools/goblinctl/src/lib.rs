pub mod assets;
pub mod contract_values;
pub mod credentials;
pub mod database;
pub mod database_host;
pub mod deployment;
pub mod environment;
pub mod files;
pub mod install;
pub mod local;
pub mod operations;
pub mod postgres;
pub mod progress;
pub mod setup;

/// The release operation supplies a build version without editing Cargo manifests.
/// `option_env!` requires a literal; setters use environment::GOBLINCTL_BUILD_VERSION.
pub const VERSION: &str = match option_env!("GOBLINCTL_BUILD_VERSION") {
    Some(version) => version,
    None => env!("CARGO_PKG_VERSION"),
};
