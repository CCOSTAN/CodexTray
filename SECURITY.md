# Security

Codex Tray deliberately delegates authentication to the local Codex app-server. It must not read, copy, log, or transmit Codex credentials.

The application opens a small set of fixed HTTPS links only after a user clicks the corresponding menu, hover-card, or notification control.

Optional organization billing access uses a separate admin key supplied through the masked **API spend settings** dialog. Windows DPAPI protects the saved key for the current user. The billing client sends the key only to the fixed OpenAI organization costs endpoint and disables redirects. The locally cached billing report contains aggregate costs. Disconnecting removes both the saved key and the report from this machine.

The application has no self-update path. Review and build new versions deliberately.

Security-sensitive changes include executable discovery, child-process launch arguments, app-server protocol handling, startup registration, and any new persistence or network behavior.
