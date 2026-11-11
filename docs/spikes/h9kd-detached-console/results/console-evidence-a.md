### git ls-remote, today (launcher with its own windowless console, like h9kd) (launch #0, from tA-arm0-git-today.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 20524 | 48704 | git.exe `"C:\Program Files\Git\ucrt64\bin\git.exe" ls-remote https://github.com/Hallmanac/hall9k-sm...` | inherited (launcher's console) |
| 46828 | 20524 | git.exe `git remote-https https://github.com/Hallmanac/hall9k-smoke.git https://github.com/Hallmana...` | inherited (launcher's console) |
| 47812 | 46828 | git-remote-https.exe `git-remote-https https://github.com/Hallmanac/hall9k-smoke.git https://github.com/Hallmana...` | inherited (launcher's console) |
| 44240 | 47812 | sh.exe `"C:/Program Files/Git/usr/bin/sh.exe" -c "'C:\\Program Files\\GitHub CLI\\gh.exe' auth git...` | inherited (launcher's console) |
| 13160 | 44240 | gh.exe `"C:\Program Files\GitHub CLI\gh.exe" auth git-credential get` | inherited (launcher's console) |
| 44344 | 47812 | sh.exe `"C:/Program Files/Git/usr/bin/sh.exe" -c "'C:\\Program Files\\GitHub CLI\\gh.exe' auth git...` | inherited (launcher's console) |
| 25376 | 44344 | gh.exe `"C:\Program Files\GitHub CLI\gh.exe" auth git-credential store` | inherited (launcher's console) |

### git ls-remote, detached (launch #0, from tA-arm1-git-detached.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 49000 | 21468 | git.exe `"C:\Program Files\Git\ucrt64\bin\git.exe" ls-remote https://github.com/Hallmanac/hall9k-sm...` | NONE |
| 13064 | 49000 | git.exe `git remote-https https://github.com/Hallmanac/hall9k-smoke.git https://github.com/Hallmana...` | NONE |
| 30496 | 13064 | git-remote-https.exe `git-remote-https https://github.com/Hallmanac/hall9k-smoke.git https://github.com/Hallmana...` | NONE |
| 8708 | 30496 | sh.exe `"C:/Program Files/Git/usr/bin/sh.exe" -c "'C:\\Program Files\\GitHub CLI\\gh.exe' auth git...` | NEW console |
| 8392 | 8708 | cygwin-console-helper.exe `"\\?\C:\Program Files\Git\usr\bin\cygwin-console-helper.exe" 0x258 0x220` | NEW console |
| 12436 | 8708 | gh.exe `"C:\Program Files\GitHub CLI\gh.exe" auth git-credential get` | NEW console |
| 33180 | 30496 | sh.exe `"C:/Program Files/Git/usr/bin/sh.exe" -c "'C:\\Program Files\\GitHub CLI\\gh.exe' auth git...` | NEW console |
| 32500 | 33180 | cygwin-console-helper.exe `"\\?\C:\Program Files\Git\usr\bin\cygwin-console-helper.exe" 0x274 0x270` | NEW console |
| 47756 | 33180 | gh.exe `"C:\Program Files\GitHub CLI\gh.exe" auth git-credential store` | NEW console |

### gh api user, today (launch #0, from tA-arm6-gh-today.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 632 | 39496 | gh.exe `"C:\Program Files\GitHub CLI\gh.exe" api user -q .login` | inherited (launcher's console) |
| 2916 | 632 | tzutil.exe `tzutil /g` | inherited (launcher's console) |

### gh api user, detached (launch #0, from tA-arm7-gh-detached.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 8632 | 41228 | gh.exe `"C:\Program Files\GitHub CLI\gh.exe" api user -q .login` | NONE |
| 43700 | 8632 | tzutil.exe `tzutil /g` | NEW console |
| 44432 | 8632 | tzutil.exe `tzutil /g` | NEW console |

### sh -c chain, today (launch #0, from tA-arm12-sh-today.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 13440 | 17616 | sh.exe `"C:\Program Files\Git\usr\bin\sh.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/una...` | inherited (launcher's console) |
| 3500 | 13440 | sh.exe `"C:\Program Files\Git\usr\bin\sh.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/una...` | inherited (launcher's console) |
| 32860 | 3500 | HOSTNAME.EXE `C:\Windows\System32\hostname.exe` | inherited (launcher's console) |
| 40400 | 13440 | sh.exe `"C:\Program Files\Git\usr\bin\sh.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/una...` | inherited (launcher's console) |
| 23380 | 40400 | uname.exe `"C:\Program Files\Git\usr\bin\uname.exe" -s` | inherited (launcher's console) |

### sh -c chain, detached (launch #0, from tA-arm13-sh-detached.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 41912 | 34372 | sh.exe `"C:\Program Files\Git\usr\bin\sh.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/una...` | inherited (launcher's console) |
| 43960 | 41912 | sh.exe `"C:\Program Files\Git\usr\bin\sh.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/una...` | NEW console |
| 45632 | 43960 | cygwin-console-helper.exe `"\\?\C:\Program Files\Git\usr\bin\cygwin-console-helper.exe" 0x220 0x21C` | NEW console |
| 4632 | 43960 | HOSTNAME.EXE `C:\Windows\System32\hostname.exe` | NEW console |
| 39148 | 41912 | sh.exe `"C:\Program Files\Git\usr\bin\sh.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/una...` | NEW console |
| 36204 | 39148 | cygwin-console-helper.exe `"\\?\C:\Program Files\Git\usr\bin\cygwin-console-helper.exe" 0x21C 0x224` | NEW console |
| 41108 | 39148 | uname.exe `"C:\Program Files\Git\usr\bin\uname.exe" -s` | NEW console |
| 11196 | 41912 | HOSTNAME.EXE `C:\Windows\System32\hostname.exe` | inherited (launcher's console) |

### bin\bash.exe -c chain, today (launch #0, from tA-arm17-bash-today.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 46912 | 41344 | bash.exe `"C:\Program Files\Git\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/uname...` | inherited (launcher's console) |
| 42676 | 46912 | bash.exe `"C:\Program Files\Git\bin\..\usr\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /us...` | inherited (launcher's console) |
| 44672 | 42676 | bash.exe `"C:\Program Files\Git\bin\..\usr\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /us...` | inherited (launcher's console) |
| 50100 | 44672 | HOSTNAME.EXE `C:\Windows\System32\hostname.exe` | inherited (launcher's console) |
| 25588 | 42676 | bash.exe `"C:\Program Files\Git\bin\..\usr\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /us...` | inherited (launcher's console) |
| 596 | 25588 | uname.exe `"C:\Program Files\Git\usr\bin\uname.exe" -s` | inherited (launcher's console) |
| 12472 | 42676 | HOSTNAME.EXE `C:\Windows\System32\hostname.exe` | inherited (launcher's console) |

### bin\bash.exe -c chain, detached (launch #0, from tA-arm18-bash-detached.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 46808 | 49860 | bash.exe `"C:\Program Files\Git\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /usr/bin/uname...` | NONE |
| 5644 | 46808 | bash.exe `"C:\Program Files\Git\bin\..\usr\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /us...` | NEW console |
| 44832 | 5644 | bash.exe `"C:\Program Files\Git\bin\..\usr\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /us...` | NEW console |
| 12208 | 5644 | bash.exe `"C:\Program Files\Git\bin\..\usr\bin\bash.exe" -c "/c/Windows/System32/hostname.exe && /us...` | NEW console |
| 4304 | 5644 | HOSTNAME.EXE `C:\Windows\System32\hostname.exe` | NEW console |

### Git for Windows launcher cmd\git.exe, today (launch #0, from tF-arm0-gitcmd-today.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 38028 | 3940 | git.exe `"C:\Program Files\Git\cmd\git.exe" --version` | inherited (launcher's console) |
| 47188 | 38028 | git.exe `git.exe --version` | inherited (launcher's console) |

### Git for Windows launcher cmd\git.exe, detached (launch #0, from tF-arm1-gitcmd-detached.log)

| pid | parent | process | console |
|---:|---:|---|---|
| 49540 | 9568 | git.exe `"C:\Program Files\Git\cmd\git.exe" --version` | NONE |
| 49012 | 49540 | git.exe `git.exe --version` | NEW console |

