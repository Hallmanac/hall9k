===heading===
## Shell limits in this session
===statement===
Shell commands are restricted in this session. You start in the checkout, so never use `cd` or `git -C`. Only a short list of read-only commands is allowed, and anything else is refused without a prompt: run one git command per call, with no pipes and no chained commands. Read files with the Read tool, and search with the Grep tool only if this session lists one; never use shell grep. Without a search tool you can read only the paths you can already name. If a command is refused, do not retry a variation of it: use what you can read, and say in your report what you could not check or search as a result.
