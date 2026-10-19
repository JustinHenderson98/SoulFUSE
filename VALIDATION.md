# Validation record

Prototype 0.4, October 19, 2026 UTC.

- .NET SDK 10.0.402; target net10.0; release build: zero warnings/errors.
- Native bridge compiled with GCC `-std=c11 -O2 -Wall -Wextra -Werror -fPIC -shared`, official libfuse 3.14.0 headers and installed libfuse3 runtime. Normal host builds use distribution headers via pkg-config.
- 29 tests through a local HTTP simulation of slskd and real native file operations; see TEST-RESULTS.txt.
- Tests include single-copy registration, deletion of the actual download, refetch after eviction with old events unavailable, external-deletion recovery preserving pins, active descriptor reads after unlink, offline restart, inode-replacement/symlink rejection, untracked-file preservation, legacy-copy preservation, plus metadata/concurrency/timeout/budget/HTTP behaviors from 0.1.
- No real Soulseek account was used for automated tests. The user confirmed 0.2 eviction and refetch against deployed slskd 0.26.1.
- User previously confirmed 0.1 browse/fetch/FUSE reads on WSL2. No kernel FUSE mount or Samba test was performed for 0.4 in this build environment.
- API behavior inspected in upstream slskd commit 90a0d24a84a132a998b937b6a60de945caa267cc, including DELETE downloads/{username}/{id}?remove=true. This clears the completed transfer record; file eviction is done separately by the adapter.

Path tests cover readable spaces/punctuation/Unicode, literal percent sequences without collisions, UTF-8 byte limits, traversal rejection, and offline catalog migration retaining cached data and pins. HTTP username escaping continues to be exercised through the simulated server.

Prefix tests cover two independent roots, nested-prefix preservation, overlapping directory fallback, file/directory conflicts, original protocol filenames during enqueue, and pinned offline-cache reuse after restart. The user confirmed readable mounted paths in 0.3; the 0.4 projection still requires host acceptance.
