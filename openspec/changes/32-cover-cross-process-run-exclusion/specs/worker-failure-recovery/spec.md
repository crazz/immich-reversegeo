## MODIFIED Requirements

### Requirement: Fault containment is bounded and does not impersonate user cancellation
After a terminal-preventing startup, protocol, sink, projection, or output fault, the control plane SHALL begin or join one exact-session internal containment operation if the process remains alive. It SHALL reuse the block-28 owner's single deadline, whole-tree kill, exit/drain, and disposal mechanics without creating a second timer or process owner; it SHALL distinguish fault containment from user Stop and host shutdown, SHALL NOT send further protocol input after output/protocol safety is lost, and SHALL classify only after containment settles. An accepted kill SHALL be failure cleanup unless an earlier exact-session Stop or shutdown intent independently authorizes Cancelled. Kill failure SHALL retain ownership and surface Failed rather than release an unsettled process. A control-plane-created finalization receipt SHALL prove the committed UI winner without becoming evidence that the worker emitted a terminal; raw exit pairing, protocol-after-terminal, and projection-after-terminal checks SHALL apply only to worker-terminal provenance. Independent output/input transport, kill, cleanup, and shutdown anomalies SHALL remain reportable for either receipt origin. When physical controller-input close fails before process exit after a fully validated terminal is committed, the control plane SHALL record the typed input-close containment fact, preserve that committed terminal and its receipt, begin or join the same bounded containment lifecycle without retrying the close or self-joining its shared attempt, and report InputTransport only as a supplementary anomaly.

#### Scenario: Pre-ready timeout worker remains alive
- **WHEN** readiness times out and the worker does not exit
- **THEN** one internal fault-containment operation reaches the shared grace deadline, kills the process tree once, drains both pipes, and permits one Failed finality

#### Scenario: Post-acceptance protocol fault worker remains alive
- **WHEN** a terminal-preventing protocol or sink fault is latched after execute and the worker stays alive
- **THEN** containment sends no unsafe follow-up protocol frame, kills at most once after the shared deadline, drains fully, and finalizes Failed

#### Scenario: Terminal input close fails before physical exit
- **WHEN** a fully validated terminal has committed, the process remains alive, and the controller's one owned physical input-close attempt fails
- **THEN** the committed terminal and receipt remain authoritative, exactly one existing bounded containment lifecycle owns physical finality, and InputTransport is supplementary without a terminal rewrite, duplicate projection, or duplicate close attempt

#### Scenario: Earlier sink fault cannot hide later terminal input-close failure
- **WHEN** a sink failure was already recorded as the first terminal-preventing observation, then a fully validated terminal is accepted and its owned physical input-close attempt fails before exit
- **THEN** the finalizer consumes the first fault once, continues monitoring the independent close-failure fact, and joins the same containment owner for physical finality; the original failure remains the first containment cause, any accepted terminal or receipt remains authoritative, and InputTransport remains independently reportable

#### Scenario: Abrupt death is finalized by the control plane
- **WHEN** a worker exits without a terminal and the control plane commits the Failed crash result
- **THEN** the receipt preserves that UI result without creating terminal-exit, protocol-after-terminal, or projection-after-terminal anomalies, while any independent late cleanup, transport, kill, or shutdown anomaly remains visible
