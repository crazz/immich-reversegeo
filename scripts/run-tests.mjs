#!/usr/bin/env node

import { randomUUID } from "node:crypto";
import { access, mkdir, open, stat } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryDirectory = path.resolve(scriptDirectory, "..");
const defaultProject = "tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj";
const normalExclusions = "TestCategory!=Integration&TestCategory!=Performance";
const performanceExclusion = "TestCategory!=Performance";
const performanceSelection = "TestCategory=Performance&TestCategory!=Integration";
const cleanupGraceMilliseconds = 2_000;

function printHelp()
{
    console.log("Usage: node scripts/run-tests.mjs [--integration | --performance] [--filter expression] [--project path]");
    console.log("");
    console.log("Default and Integration runs exclude Performance. --performance explicitly selects hermetic Performance tests.");
}

function failUsage(message)
{
    console.error(`Error: ${message}`);
    console.error("Use --help for usage.");
    process.exitCode = 2;
}

function parseArguments(argumentsToParse)
{
    const options = {
        integration: false,
        performance: false,
        filter: undefined,
        project: undefined
    };

    for (let index = 0; index < argumentsToParse.length; index += 1)
    {
        const argument = argumentsToParse[index];

        if (argument === "--help")
        {
            if (argumentsToParse.length !== 1)
            {
                throw new Error("--help cannot be combined with other arguments.");
            }

            return { help: true };
        }

        if (argument === "--integration" || argument === "--performance")
        {
            const mode = argument.slice(2);
            if (options.integration || options.performance)
            {
                throw new Error("Choose --integration or --performance once; the modes cannot be combined.");
            }

            options[mode] = true;
            continue;
        }

        if (argument === "--filter" || argument === "--project")
        {
            const optionName = argument.slice(2);
            const value = argumentsToParse[index + 1];

            if (value === undefined || value.startsWith("--") || value.length === 0)
            {
                throw new Error(`${argument} requires a non-empty value.`);
            }

            if (options[optionName] !== undefined)
            {
                throw new Error(`${argument} may only be provided once.`);
            }

            options[optionName] = value;
            index += 1;
            continue;
        }

        throw new Error(`Unknown argument: ${argument}`);
    }

    return options;
}

function quoteForDisplay(value)
{
    return `'${value.replaceAll("'", "'\\''")}'`;
}

async function validateProject(project)
{
    const absoluteProject = path.resolve(repositoryDirectory, project);
    const relativeProject = path.relative(repositoryDirectory, absoluteProject);

    if (relativeProject.startsWith("..") || path.isAbsolute(relativeProject))
    {
        throw new Error("--project must point to a project inside this repository.");
    }

    if (path.extname(absoluteProject).toLowerCase() !== ".csproj")
    {
        throw new Error("--project must point to an existing .csproj file.");
    }

    try
    {
        const projectStats = await stat(absoluteProject);
        if (!projectStats.isFile())
        {
            throw new Error("not a file");
        }

        await access(absoluteProject);
    }
    catch
    {
        throw new Error(`--project does not exist: ${project}`);
    }

    return relativeProject;
}

function buildTestArguments(options, project)
{
    const testArguments = ["test"];

    if (options.integration)
    {
        testArguments.push("--settings", "integration.runsettings");
    }
    else if (options.performance)
    {
        testArguments.push("--settings", "performance.runsettings",
            "--results-directory", "_out/performance/worker-memory-soak/test-results");
    }

    if (project !== undefined)
    {
        testArguments.push("--project", project);
    }

    let filter;
    if (options.filter !== undefined)
    {
        const selection = options.performance ? performanceSelection
            : options.integration ? performanceExclusion : normalExclusions;
        filter = `(${options.filter})&${selection}`;
    }
    else if (options.integration)
    {
        filter = `TestCategory=Integration&${performanceExclusion}`;
    }
    else if (options.performance)
    {
        filter = performanceSelection;
    }
    else
    {
        filter = normalExclusions;
    }

    testArguments.push("--filter", filter);
    return testArguments;
}

function createRunLogPath()
{
    const timestamp = new Date().toISOString().replaceAll(":", "-").replaceAll(".", "-");
    return path.join(repositoryDirectory, "_out", "agent-tests", `${timestamp}-${process.pid}-${randomUUID()}.log`);
}

function terminateOwnedProcessGroup(child, signal)
{
    if (child === undefined || child.pid === undefined)
    {
        return;
    }

    try
    {
        if (process.platform === "win32")
        {
            child.kill(signal);
        }
        else
        {
            process.kill(-child.pid, signal);
        }
    }
    catch (error)
    {
        if (error.code !== "ESRCH")
        {
            console.error(`Unable to stop the test process: ${error.message}`);
        }
    }
}

async function runDotnet(testArguments)
{
    const logPath = createRunLogPath();
    let logFile;
    try
    {
        await mkdir(path.dirname(logPath), { recursive: true });
        logFile = await open(logPath, "wx");
    }
    catch (error)
    {
        console.error(`Unable to open test log: ${error.message}`);
        return 1;
    }

    const logStream = logFile.createWriteStream({ autoClose: true });
    const command = `dotnet ${testArguments.map(quoteForDisplay).join(" ")}`;
    const startedAt = process.hrtime.bigint();
    let interruptionSignal;
    let child;
    let logError;
    let forceStopTimer;
    let childSettled = false;
    let cleanupFinished;
    let resolveCleanup;

    const writeLog = (text) =>
    {
        if (logError === undefined && !logStream.destroyed)
        {
            try
            {
                logStream.write(text);
            }
            catch (error)
            {
                handleLogError(error);
            }
        }
    };

    const stopOwnedProcess = (signal, reason) =>
    {
        if (child === undefined || childSettled)
        {
            return;
        }

        terminateOwnedProcessGroup(child, signal);

        if (forceStopTimer === undefined)
        {
            cleanupFinished = new Promise((resolve) =>
            {
                resolveCleanup = resolve;
            });
            forceStopTimer = setTimeout(() =>
            {
                writeLog(`Forcing owned test process group shutdown after ${reason}.\n`);
                terminateOwnedProcessGroup(child, "SIGKILL");
                forceStopTimer = undefined;
                resolveCleanup();
            }, cleanupGraceMilliseconds);
        }
    };

    const handleLogError = (error) =>
    {
        if (logError !== undefined)
        {
            return;
        }

        logError = error;
        console.error(`Test log failed: ${error.message}`);
        stopOwnedProcess("SIGTERM", "a log write failure");
    };

    const writeOutput = (stream, chunk) =>
    {
        stream.write(chunk);
        writeLog(chunk);
    };

    const onSignal = (signal) =>
    {
        if (interruptionSignal !== undefined)
        {
            return;
        }

        interruptionSignal = signal;
        console.error(`Received ${signal}; stopping the owned test process group.`);
        writeLog(`Received ${signal}; stopping the owned test process group.\n`);
        stopOwnedProcess(signal, interruptionSignal);
    };

    process.on("SIGINT", onSignal);
    process.on("SIGTERM", onSignal);
    logStream.once("error", handleLogError);

    let outcome;
    try
    {
        writeLog(`Command: ${command}\n`);
        console.log(`Log: ${logPath}`);
        child = spawn("dotnet", testArguments, {
            cwd: repositoryDirectory,
            detached: process.platform !== "win32",
            env: { ...process.env, LC_ALL: "en_US.UTF-8" },
            shell: false,
            stdio: ["ignore", "pipe", "pipe"]
        });

        if (interruptionSignal !== undefined)
        {
            stopOwnedProcess(interruptionSignal, interruptionSignal);
        }
        else if (logError !== undefined)
        {
            stopOwnedProcess("SIGTERM", "a log write failure");
        }

        child.stdout.on("data", (chunk) => writeOutput(process.stdout, chunk));
        child.stderr.on("data", (chunk) => writeOutput(process.stderr, chunk));

        outcome = await new Promise((resolve) =>
        {
            let settled = false;
            const settle = (value) =>
            {
                if (!settled)
                {
                    settled = true;
                    resolve(value);
                }
            };

            child.once("error", (error) => settle({ error }));
            child.once("close", (code, signal) => settle({ code, signal }));
        });
    }
    catch (error)
    {
        outcome = { error };
    }

    childSettled = true;
    if (cleanupFinished !== undefined)
    {
        await cleanupFinished;
    }

    let exitCode;
    if (interruptionSignal !== undefined)
    {
        exitCode = interruptionSignal === "SIGINT" ? 130 : 143;
    }
    else if (logError !== undefined)
    {
        exitCode = 1;
    }
    else if (outcome.error !== undefined)
    {
        exitCode = 1;
    }
    else if (outcome.signal !== null)
    {
        exitCode = 1;
    }
    else
    {
        exitCode = outcome.code ?? 1;
    }

    if (outcome.error !== undefined)
    {
        const failureMessage = `Unable to start dotnet: ${outcome.error.message}`;
        writeLog(`${failureMessage}\n`);
        console.error(failureMessage);
    }

    const elapsedSeconds = Number(process.hrtime.bigint() - startedAt) / 1_000_000_000;
    const summary = `Command: ${command}\nExit: ${exitCode}; duration: ${elapsedSeconds.toFixed(1)}s; log: ${logPath}\n`;
    writeLog(summary);

    const logFlushed = await new Promise((resolve) =>
    {
        if (logStream.destroyed)
        {
            resolve(false);
            return;
        }

        logStream.once("error", () => resolve(false));
        logStream.end(() => resolve(true));
    });

    if (!logFlushed || logError !== undefined)
    {
        exitCode = 1;
    }

    process.removeListener("SIGINT", onSignal);
    process.removeListener("SIGTERM", onSignal);
    console.log(`Command: ${command}`);
    console.log(`Exit: ${exitCode}; duration: ${elapsedSeconds.toFixed(1)}s; log: ${logPath}`);

    return exitCode;
}

async function main()
{
    let options;
    try
    {
        options = parseArguments(process.argv.slice(2));
    }
    catch (error)
    {
        failUsage(error.message);
        return;
    }

    if (options.help)
    {
        printHelp();
        return;
    }

    let project;
    try
    {
        project = options.project === undefined
            ? (options.filter === undefined && !options.integration && !options.performance ? undefined : await validateProject(defaultProject))
            : await validateProject(options.project);
    }
    catch (error)
    {
        failUsage(error.message);
        return;
    }

    process.exitCode = await runDotnet(buildTestArguments(options, project));
}

await main();
