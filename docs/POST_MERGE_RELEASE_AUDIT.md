# Optimized Release Audit

- Final server workflow commit: `5274ef38efbb785fd1776e0ce5c659d55a0ce40e`
- Result: **false**

## Final server workflow state
```text
30071339170	completed	failure	https://github.com/caox233/SCBL/actions/runs/30071339170
```

## Client stable assets
- client-release-manifest.json

## Client immutable assets
- SCBL-Client-v0.6.3-win-x86.zip
- SCBL-Client-v0.6.3-win-x86.zip.sha256
- SHA256SUMS.txt
- client-release-manifest.json

## Server stable assets
- install-server.sh
- server-tool-release-manifest.json

## Server immutable assets
- SCBL-Server-Tool-latest-linux-x86_64.tar.gz
- SCBL-Server-Tool-latest-linux-x86_64.tar.gz.sha256
- SCBL-Server-Tool-v0.6.9-linux-x86_64.tar.gz
- SCBL-Server-Tool-v0.6.9-linux-x86_64.tar.gz.sha256
- SHA256SUMS.txt
- install-server.sh
- server-tool-release-manifest.json

## server-immutable difference
```diff
--- audit/expected-server-immutable.txt	2026-07-24 06:11:24.803675251 +0000
+++ audit/server-immutable-assets.txt	2026-07-24 06:11:24.797675221 +0000
@@ -1,3 +1,5 @@
+SCBL-Server-Tool-latest-linux-x86_64.tar.gz
+SCBL-Server-Tool-latest-linux-x86_64.tar.gz.sha256
 SCBL-Server-Tool-v0.6.9-linux-x86_64.tar.gz
 SCBL-Server-Tool-v0.6.9-linux-x86_64.tar.gz.sha256
 SHA256SUMS.txt
```
