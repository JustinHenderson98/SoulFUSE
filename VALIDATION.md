# Validation record

Prototype 0.1, October 18, 2026.

- .NET SDK used: 10.0.402; target framework: net10.0.
- C# release build: zero warnings and errors.
- Native build: GCC, `-std=c11 -O2 -Wall -Wextra -Werror -fPIC -shared` against libfuse3 3.14.0 headers and the installed `libfuse3.so.3` runtime.
- Build environment did not provide the libfuse development package; headers were obtained from official libfuse tag `fuse-3.14.0` solely for compilation. They are not bundled. On a normal Linux host, `build.sh` uses the distribution's libfuse3 headers through pkg-config.
- `bash -n` passed for build, test, and mount-smoke scripts.
- CLI help executed successfully.
- 14 automated tests passed through a simulated slskd HTTP API and actual native safe-file-open bridge; see TEST-RESULTS.txt.
- No FUSE kernel device was available, so actual mount callbacks and SMB behavior were not exercised.
- Unix-domain sockets are prohibited in the build environment, so live CLI-to-daemon socket routing remains unverified. Offline CLI operation was tested.
- No real Soulseek peer/account was accessed and no user files were downloaded.

The source/API inspection and test fixture are grounded in slskd commit `90a0d24a84a132a998b937b6a60de945caa267cc`. Compatibility with a deployed slskd release remains an integration gate, particularly the completion Events API and grouped transfer responses.
