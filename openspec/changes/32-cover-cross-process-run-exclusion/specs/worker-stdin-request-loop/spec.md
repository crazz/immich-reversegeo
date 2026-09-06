## MODIFIED Requirements

### Requirement: The command pump has structured lifetime and disposal
The accepted request lease SHALL expose completion/finality for its background control pump. The pump SHALL run only from readiness until pre-request finality, clean post-request EOF, fatal input failure, terminal/host shutdown, or lease disposal. Terminal notification and disposal SHALL stop accepting new control work without waiting indefinitely for future stdin bytes, unblock any pending read through the owned stream's cancellation/disposal path or through peer EOF after the controller half-closes input for a fully validated terminal, await the pump's completion exactly once, and dispose decoder/buffer/cancellation resources. Local `Console.OpenStandardInput` cancellation or disposal alone SHALL NOT be promised to interrupt a pending native read; the accepted terminal's paired controller input half-close provides the normal transport completion path. Expected shutdown cancellation SHALL not replace an earlier EOF/failure outcome or be reported as a reader fault. The worker SHALL not wait after terminal for a future cancel merely to observe post-terminal behavior.

#### Scenario: Terminal occurs while read is pending
- **WHEN** execution reaches terminal while the control pump is blocked waiting for more input and the controller records that terminal
- **THEN** peer EOF or owned finalization unblocks and awaits the pump before its resources are disposed and host shutdown completes

#### Scenario: Pump already completed
- **WHEN** clean EOF or an input failure completed the pump before execution terminal
- **THEN** lease finalization observes the recorded outcome and disposes resources without restarting or double-awaiting the reader

#### Scenario: Shutdown cancellation unblocks input
- **WHEN** host shutdown or lease disposal cancels a pending read
- **THEN** the pump settles as expected disposal unless a prior primary input outcome was already recorded
