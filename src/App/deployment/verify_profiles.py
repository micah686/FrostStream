#!/usr/bin/env python3
"""Check real AppHost publish output. Requires PyYAML; never prints parameter secrets."""
import argparse
from pathlib import Path
import yaml


def check(profile: Path, mode: str, live_chat: bool):
    document = yaml.safe_load((profile / "docker-compose.yaml").read_text())
    services = document["services"]
    parameters = dict(line.split("=", 1) for line in (profile / ".env").read_text().splitlines()
                      if line and not line.startswith("#") and "=" in line)
    assert parameters.get("TYPESENSE_API_KEY", "").strip("\"'"), f"{mode}: unpopulated secret parameter"
    if mode == "Lite":
        for key in ("LITE_DATABASE_PATH", "LITE_BACKUP_DIRECTORY", "LITE_KEYS_PATH"):
            assert parameters.get(key, "").strip("\"'").startswith("/"), f"{mode}: invalid {key}"

    required = {"typesense", "pot-provider", "frontend"}
    full = {"nats", "postgres", "openbao", "authentik", "openfga", "backupservice",
            "databridge", "webapi", "worker", "mediaprocessor", "scheduler"}
    assert required <= services.keys(), f"{mode}: missing supporting services"
    assert ("clickhouse" in services) == live_chat, f"{mode}: optional ClickHouse mismatch"
    if mode == "Lite":
        assert services.keys() == required | {"lite"} | ({"clickhouse"} if live_chat else set()), services.keys()
        env = services["lite"]["environment"]
        assert env["Deployment__Mode"] == "Lite"
        assert env["Persistence__Sqlite__Enabled"] == "true"
        assert env["Persistence__Sqlite__Path"] == "${LITE_DATABASE_PATH}"
        assert env["Backup__Directory"] == "${LITE_BACKUP_DIRECTORY}"
        assert env["Secrets__Local__KeyRingPath"] == "${LITE_KEYS_PATH}"
        assert not any(key.startswith(("OpenBao", "OpenFga", "BackupService", "ConnectionStrings")) for key in env)
        assert any(v["target"] == "/data" for v in services["lite"]["volumes"])
        assert services["frontend"]["environment"]["WEBAPI_UPSTREAM"] == "http://lite:8080"
    else:
        assert full <= services.keys(), f"Full: missing services {full - services.keys()}"
        assert "lite" not in services
        assert services["webapi"]["environment"]["BackupService__BaseUrl"].startswith("http://backupservice:")
        assert services["frontend"]["environment"]["WEBAPI_UPSTREAM"] == "http://webapi:8080"
    for name, service in services.items():
        assert set(service.get("depends_on", {})) <= services.keys(), f"{mode}/{name}: dangling dependency"
        if "build" in service:
            build = service["build"]
            context = (profile / build["context"]).resolve()
            assert (context / build["dockerfile"]).is_file(), f"{mode}/{name}: nonportable build path"
        for mount in service.get("volumes", []):
            if mount.get("type") == "bind" and "$" not in mount["source"]:
                assert (profile / mount["source"]).exists(), f"{mode}/{name}: missing bind source"
    print(f"{mode}: service graph, adapter configuration, storage and portable build paths passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live-chat", action="store_true")
    args = parser.parse_args()
    app = Path(__file__).resolve().parent.parent
    for mode in ("Full", "Lite"):
        check(app / f"docker-compose-{mode.lower()}", mode, args.live_chat)
