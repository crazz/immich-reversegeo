# Validate the successful Run-once console rendering without retaining arbitrary
# stderr as evidence. The five lifecycle records are the only allowed content.
BEGIN {
    record = 0
    failed = 0
    pid = ""
}

function bounded_number(value, maximum) {
    return value ~ /^(0|[1-9][0-9]*)$/ && (length(value) < length(maximum) ||
        (length(value) == length(maximum) && ("x" value) <= ("x" maximum)))
}

{
    if (NR % 2 == 1) {
        record++
        if ($0 != "info: ImmichReverseGeo.Lifecycle[" (6600 + record) "]" || record > 5) {
            failed = 1
        }
        next
    }

    line = $0
    sub(/^[[:space:]]+/, "", line)
    if (record == 1) {
        if (line !~ /^Deployment mode selected deployment_mode=run-once application_role=run-once process_id=[1-9][0-9]*$/) {
            failed = 1
        }
        split(line, fields, "process_id=")
        pid = fields[2]
        if (!bounded_number(pid, "2147483647")) {
            failed = 1
        }
    } else {
        common = " application_role=run-once deployment_mode=run-once process_id=" pid
        if (record == 2 && line != "Role starting" common) {
            failed = 1
        }
        if (record == 3 && line !~ ("^Role ready" common " readiness_kind=run-once-initialized startup_duration_ms=[0-9]+$")) {
            failed = 1
        }
        if (record == 4 && line != "Role stopping" common " stop_reason=completed") {
            failed = 1
        }
        if (record == 5 && line !~ ("^Role stopped" common " process_outcome=completed process_duration_ms=[0-9]+ stop_duration_ms=[0-9]+$")) {
            failed = 1
        }
    }

    count = split(line, fields, " ")
    for (i = 1; i <= count; i++) {
        if (fields[i] ~ /_duration_ms=/) {
            sub(/^[^=]+=/, "", fields[i])
            if (!bounded_number(fields[i], "9223372036854775807")) {
                failed = 1
            }
        }
    }
}

END {
    if (failed || NR != 10 || record != 5 || pid !~ /^[1-9][0-9]*$/) {
        exit 1
    }
}
