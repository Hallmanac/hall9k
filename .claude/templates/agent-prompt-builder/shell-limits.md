===heading===
## Shell limits in this session
===statement===
Shell commands are restricted in this session. You start in the checkout, so never use `cd` or `git -C`. Only a short list of read-only commands is allowed, and anything else is refused without a prompt: run one git command per call, with no pipes and no chained commands. Search and read with the Grep and Read tools, never shell grep. If a command is refused, do not retry a variation of it: use what you can read, and say in your report what you could not check as a result.
