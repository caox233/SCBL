from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    target = Path(path)
    text = target.read_text(encoding="utf-8")
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"patch anchor not found in {path}: {old[:160]!r}")
    target.write_text(text.replace(old, new, 1), encoding="utf-8")


manager = "server/install_public_server.sh"

replace_once(
    manager,
    '  local version tag package base expected actual cmp tmpdir extract_root manager_new control_new version_new\n  local backup_root control_changed=0 binary_check_new branch_new package_root\n',
    '  local version tag package base expected actual cmp tmpdir extract_root manager_new control_new update_new version_new\n  local backup_root control_changed=0 binary_check_new branch_new package_root\n',
)

replace_once(
    manager,
    '''  package_root="$(find "$extract_root" -mindepth 1 -maxdepth 1 -type d -name 'SCBL-Server-Tool-v*-linux-x86_64' -print -quit)"
  manager_new="${package_root}/install_public_server.sh"
  control_new="${package_root}/scbl_control_plane.py"
  version_new="${package_root}/VERSION_SERVER_TOOL"
  [[ -f "$manager_new" && -f "$control_new" && -f "$version_new" ]] || {
    rm -rf "$tmpdir"; echo "服务端工具包缺少必要文件。"; return 1;
  }
''',
    '''  package_root="$(find "$extract_root" -mindepth 1 -maxdepth 1 -type d -name 'SCBL-Server-Tool-v*-linux-x86_64' -print -quit)"
  manager_new="${package_root}/install_public_server.sh"
  control_new="${package_root}/scbl_control_plane.py"
  update_new="${package_root}/scbl_update_server.py"
  version_new="${package_root}/VERSION_SERVER_TOOL"
  [[ -f "$manager_new" && -f "$control_new" && -f "$update_new" && -f "$version_new" ]] || {
    rm -rf "$tmpdir"; echo "服务端工具包缺少必要文件。"; return 1;
  }
''',
)

replace_once(
    manager,
    '  validate_manager_script_file "$manager_new"\n  python3 -m py_compile "$control_new"\n',
    '  validate_manager_script_file "$manager_new"\n  python3 -m py_compile "$control_new" "$update_new"\n',
)

replace_once(
    manager,
    '''  [[ -f "$MANAGER_DIR/VERSION_SERVER_TOOL" ]] && cp -a "$MANAGER_DIR/VERSION_SERVER_TOOL" "$backup_root/VERSION_SERVER_TOOL"
  [[ -f "$SCBL_ROOT/server/scbl_control_plane.py" ]] && cp -a "$SCBL_ROOT/server/scbl_control_plane.py" "$backup_root/scbl_control_plane.py"
''',
    '''  [[ -f "$MANAGER_DIR/VERSION_SERVER_TOOL" ]] && cp -a "$MANAGER_DIR/VERSION_SERVER_TOOL" "$backup_root/VERSION_SERVER_TOOL"
  [[ -f "$MANAGER_DIR/scbl_update_server.py" ]] && cp -a "$MANAGER_DIR/scbl_update_server.py" "$backup_root/scbl_update_server.py"
  [[ -f "$SCBL_ROOT/server/scbl_control_plane.py" ]] && cp -a "$SCBL_ROOT/server/scbl_control_plane.py" "$backup_root/scbl_control_plane.py"
''',
)

replace_once(
    manager,
    '''    install -m 0755 "$manager_new" "$MANAGER_SCRIPT"
    install -m 0644 "$version_new" "$MANAGER_DIR/VERSION_SERVER_TOOL"
    install -d -m 0755 "$SCBL_ROOT/server"
''',
    '''    install -m 0755 "$manager_new" "$MANAGER_SCRIPT"
    install -m 0644 "$version_new" "$MANAGER_DIR/VERSION_SERVER_TOOL"
    install -m 0644 "$update_new" "$MANAGER_DIR/scbl_update_server.py"
    install -d -m 0755 "$SCBL_ROOT/server"
''',
)

replace_once(
    manager,
    '''    [[ -f "$backup_root/install_public_server.sh" ]] && install -m 0755 "$backup_root/install_public_server.sh" "$MANAGER_SCRIPT"
    [[ -f "$backup_root/VERSION_SERVER_TOOL" ]] && install -m 0644 "$backup_root/VERSION_SERVER_TOOL" "$MANAGER_DIR/VERSION_SERVER_TOOL"
    [[ -f "$backup_root/scbl_control_plane.py" ]] && install -m 0644 "$backup_root/scbl_control_plane.py" "$SCBL_ROOT/server/scbl_control_plane.py"
''',
    '''    [[ -f "$backup_root/install_public_server.sh" ]] && install -m 0755 "$backup_root/install_public_server.sh" "$MANAGER_SCRIPT"
    [[ -f "$backup_root/VERSION_SERVER_TOOL" ]] && install -m 0644 "$backup_root/VERSION_SERVER_TOOL" "$MANAGER_DIR/VERSION_SERVER_TOOL"
    [[ -f "$backup_root/scbl_update_server.py" ]] && install -m 0644 "$backup_root/scbl_update_server.py" "$MANAGER_DIR/scbl_update_server.py"
    [[ -f "$backup_root/scbl_control_plane.py" ]] && install -m 0644 "$backup_root/scbl_control_plane.py" "$SCBL_ROOT/server/scbl_control_plane.py"
''',
)

print("Server Tool v1.0.3 manager hotfix applied")
