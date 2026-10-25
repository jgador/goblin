#!/usr/bin/env bash
set -euo pipefail

# Report a cleanup error and stop the script.
clean_fail() {
    printf 'dev-reset: %s\n' "$1" >&2
    exit 1
}

# Reject symbolic links in the target and its parent directories.
clean_no_links() {
    local path=$1
    while [[ $path != / ]]; do
        [[ ! -L $path ]] || clean_fail 'A cleanup path uses a symbolic link; review it first.'
        path=$(dirname -- "$path")
    done
}

# Load mounted paths from the system mount table, decoding escaped characters.
clean_read_mounts() {
    local line mount
    local -a fields
    mounts=()
    [[ -r $system/proc/self/mountinfo ]] || clean_fail 'Cannot inspect filesystem mounts.'
    while IFS= read -r line; do
        read -r -a fields <<< "$line"
        ((${#fields[@]} >= 10)) || clean_fail 'Cannot parse filesystem mounts.'
        printf -v mount '%b' "${fields[4]}"
        mounts+=("$mount")
    done < "$system/proc/self/mountinfo"
    ((${#mounts[@]} > 0)) || clean_fail 'Filesystem mount inventory is empty.'
}

# Refuse cleanup roots that use symbolic links or contain filesystem mounts.
clean_check_directory() {
    local path=$1 mount
    clean_no_links "$path"
    for mount in "${mounts[@]}"; do
        [[ $mount != "$path" && $mount != "$path/"* ]] ||
            clean_fail 'A cleanup directory contains a mount; review it first.'
    done
}

# Clear one runtime directory while preserving tracked files and their parents.
clean_remove_runtime() {
    local runtime=$1 path=$repo/$1 entry relative keep file
    [[ -e $path ]] || return 0
    clean_check_directory "$path"
    [[ -z ${tracked[$runtime]:-} ]] || return 0
    keep=0
    for file in "${!tracked[@]}"; do
        [[ $file != "$runtime/"* ]] || keep=1
    done
    if (( ! keep )); then
        rm -rf --one-file-system -- "$path"
        return
    fi
    # Keep tracked files and their parents; remove untracked state without following links.
    find -P "$path" -xdev -depth -print0 > "$scratch/entries" || clean_fail 'Cannot enumerate runtime files.'
    while IFS= read -r -d '' entry; do
        relative=${entry#"$repo/"} keep=0
        for file in "${!tracked[@]}"; do
            if [[ $file == "$relative" || $file == "$relative/"* ]]; then keep=1; break; fi
        done
        ((keep)) || rm -rf --one-file-system -- "$entry"
    done < "$scratch/entries"
}

# Stop Goblin services, uninstall local k3s, and remove installation files.
clean_installation() {
    local owner=$system/var/lib/goblin/local-test/config.json unit path remaining
    local uninstall=$system/usr/local/bin/k3s-uninstall.sh
    local -a units=(goblin-installer.service goblin-setup.service goblin-setup-control.socket
        goblin-setup-control@.service goblin-local.socket goblin-local-postgres.socket
        goblin-local.service goblin-local-postgres.service)
    local -a host_directories=(etc/rancher/k3s var/lib/rancher/k3s var/lib/cni opt/goblin/setup var/lib/goblin)
    clean_no_links "$owner"
    for path in "${host_directories[@]}" var/lib/kubelet usr/local/bin/k3s usr/local/bin/k3s-uninstall.sh \
        usr/local/bin/k3s-killall.sh var/log/goblin-bootstrap.log var/log/goblin-installer.log \
        etc/systemd/system/goblin-installer.service.d/local-test.conf \
        etc/systemd/system/goblin-setup.service.d/listener.conf; do
        clean_no_links "$system/$path"
    done
    for unit in "${units[@]}" k3s.service; do clean_no_links "$system/etc/systemd/system/$unit"; done
    if [[ ! -e $owner ]]; then
        for path in "${host_directories[@]}" var/lib/kubelet usr/local/bin/k3s usr/local/bin/k3s-uninstall.sh usr/local/bin/k3s-killall.sh; do
            [[ ! -e $system/$path ]] || clean_fail 'Local installation files remain without checkout ownership.'
        done
        for unit in "${units[@]}" k3s.service; do
            [[ ! -e $system/etc/systemd/system/$unit ]] || clean_fail 'Local services remain without checkout ownership.'
        done
        return
    fi
    command -v jq >/dev/null || clean_fail 'Install jq in WSL to check installation ownership.'
    jq -e --arg repo "$repo" '.mode == "direct" and .repo == $repo' "$owner" >/dev/null 2>&1 ||
        clean_fail 'The local installation belongs to another checkout.'
    if [[ ! -x $uninstall && ( -e $system/usr/local/bin/k3s || -e $system/etc/systemd/system/k3s.service ) ]]; then
        clean_fail 'K3s is installed but its uninstaller is missing.'
    fi
    # Stop installer recovery before the setup UI and socket-activated forwarders.
    for unit in "${units[@]}"; do
        [[ -e $system/etc/systemd/system/$unit ]] || continue
        case $unit in
            goblin-local.service|goblin-local-postgres.service|goblin-setup-control.socket) systemctl stop "$unit" ;;
            # Template: its short-lived instances stop with the control socket.
            goblin-setup-control@.service) ;;
            *) systemctl disable --now "$unit" ;;
        esac
    done
    if [[ -e $system/etc/systemd/system/k3s.service ]]; then
        systemctl disable --now k3s.service
    fi
    if [[ -x $uninstall ]]; then
        env -i PATH="$PATH" "$uninstall" >/dev/null 2>&1 || clean_fail 'The local k3s uninstaller failed.'
    fi
    [[ ! -e $system/usr/local/bin/k3s && ! -e $system/etc/systemd/system/k3s.service ]] ||
        clean_fail 'K3s remains after uninstall; repository data was retained.'
    # WSL may retain an empty kubelet mount after the uninstaller finishes.
    if [[ -d $system/var/lib/kubelet ]]; then
        remaining=$(find "$system/var/lib/kubelet" -mindepth 1 -maxdepth 1 -print -quit) || clean_fail 'Cannot inspect remaining kubelet data.'
        [[ -z $remaining ]] || clean_fail 'Kubelet data remains after uninstall; repository data was retained.'
        if mountpoint --quiet "$system/var/lib/kubelet"; then umount -- "$system/var/lib/kubelet"; fi
        rmdir -- "$system/var/lib/kubelet"
    fi
    clean_read_mounts
    for path in "${host_directories[@]}"; do clean_check_directory "$system/$path"; done
    for unit in "${units[@]}"; do
        systemctl reset-failed "$unit" >/dev/null 2>&1 || true
        rm -f -- "$system/etc/systemd/system/$unit"
    done
    rm -f -- "$system/etc/systemd/system/goblin-installer.service.d/local-test.conf"
    if [[ -d $system/etc/systemd/system/goblin-installer.service.d ]]; then
        rmdir --ignore-fail-on-non-empty -- "$system/etc/systemd/system/goblin-installer.service.d"
    fi
    rm -f -- "$system/etc/systemd/system/goblin-setup.service.d/listener.conf"
    if [[ -d $system/etc/systemd/system/goblin-setup.service.d ]]; then
        rmdir --ignore-fail-on-non-empty -- "$system/etc/systemd/system/goblin-setup.service.d"
    fi
    for path in "${host_directories[@]}"; do rm -rf --one-file-system -- "$system/${path:?}"; done
    rm -f -- "$system/var/log/goblin-bootstrap.log" "$system/var/log/goblin-installer.log"
    systemctl daemon-reload
}

# Remove this checkout's local installation and known runtime directories.
dev_clean_slate() {
    local repo=$1 system=${2%/} runtime file scratch
    local -a mounts=() runtime_directories=(.goblin-platform .goblin-local .goblin-secrets .goblin-browser-test .artifacts/playwright)
    local -A tracked=()
    [[ $(id -u) == 0 ]] || clean_fail 'Installation cleanup requires sudo.'
    grep -qi microsoft "$system/proc/version" || clean_fail 'Use this helper in WSL.'
    clean_read_mounts
    for runtime in "${runtime_directories[@]}"; do clean_check_directory "$repo/$runtime"; done
    clean_check_directory "$repo/.artifacts/dev-clean-slate"
    mkdir -p -- "$repo/.artifacts/dev-clean-slate"
    scratch=$(mktemp -d "$repo/.artifacts/dev-clean-slate/run-XXXXXX")
    trap 'rm -rf --one-file-system -- "$scratch"' EXIT
    git -c safe.directory="$repo" -C "$repo" ls-files -z -- "${runtime_directories[@]}" > "$scratch/tracked" ||
        clean_fail 'Cannot inspect tracked files; runtime state has been retained.'
    while IFS= read -r -d '' file; do tracked[$file]=1; done < "$scratch/tracked"
    [[ -z ${tracked[.goblin-secrets/owner-password]:-} ]] || clean_fail 'The saved password is tracked; resolve it before full cleanup.'
    printf 'Removing the owned local cluster, development data, and saved password.\n'
    clean_installation
    clean_read_mounts
    for runtime in "${runtime_directories[@]}"; do
        clean_remove_runtime "$runtime"
        printf 'Cleared %s; tracked files preserved.\n' "$runtime"
    done
    rm -rf --one-file-system -- "$scratch"
    trap - EXIT
    printf 'Development cleanup complete.\n'
}

# Resolve the checkout and request sudo for host cleanup.
clean_main() {
    (($# == 0)) || clean_fail 'Usage: make dev-reset'
    local script repo
    script=$(realpath -- "${BASH_SOURCE[0]}")
    repo=$(git -C "$(dirname -- "$script")" rev-parse --show-toplevel)
    repo=$(realpath -- "$repo")
    if [[ $(id -u) == 0 ]]; then
        dev_clean_slate "$repo" /
    else
        sudo -- env -i PATH="$PATH" bash -c 'source "$1"; dev_clean_slate "$2" /' dev-clean-slate "$script" "$repo"
    fi
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    clean_main "$@"
fi
