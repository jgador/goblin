//! Closed installer contracts. Browser values are generated from these definitions.
macro_rules! contract_values {
    ($name:ident { $($variant:ident => $wire:literal),+ $(,)? }) => {
        #[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize, clap::ValueEnum)]
        pub enum $name {
            $(#[serde(rename = $wire)] #[value(name = $wire)] $variant),+
        }
        impl $name {
            pub const fn as_str(self) -> &'static str {
                match self { $(Self::$variant => $wire),+ }
            }
        }
        impl std::str::FromStr for $name {
            type Err = anyhow::Error;
            fn from_str(value: &str) -> anyhow::Result<Self> {
                match value {
                    $($wire => Ok(Self::$variant)),+,
                    _ => anyhow::bail!("Unsupported {} value: {value}", stringify!($name))
                }
            }
        }
    };
}

contract_values!(SetupAction {
    Init => "init", Begin => "begin", Start => "start", Detail => "detail",
    PublicUrl => "public-url", Complete => "complete", FailStep => "fail-step",
    Handoff => "handoff", Failed => "failed", Ready => "ready"
});
contract_values!(SetupStatus {
    Waiting => "waiting", Running => "running", Failed => "failed", Ready => "ready"
});
contract_values!(SetupPhase {
    Installing => "installing", Activating => "activating", Complete => "complete"
});
contract_values!(SetupStepStatus {
    Waiting => "waiting", Running => "running", Complete => "complete", Failed => "failed"
});
contract_values!(SetupStep {
    Prepare => "prepare", K3s => "k3s", CertManager => "cert-manager", Sandbox => "sandbox",
    Image => "image", Prefetch => "prefetch", Database => "database", Import => "import",
    Migrate => "migrate", Deploy => "deploy", Verify => "verify", Activate => "activate"
});

impl SetupStep {
    pub const fn label(self) -> &'static str {
        match self {
            Self::Prepare => "Prepare installation",
            Self::K3s => "Install Kubernetes",
            Self::CertManager => "Install certificate manager",
            Self::Sandbox => "Install Agent Sandbox",
            Self::Image => "Build Goblin",
            Self::Prefetch => "Download container images",
            Self::Database => "Prepare PostgreSQL",
            Self::Import => "Import Goblin image",
            Self::Migrate => "Apply database migrations",
            Self::Deploy => "Deploy Goblin",
            Self::Verify => "Check application readiness",
            Self::Activate => "Open Goblin",
        }
    }
}
