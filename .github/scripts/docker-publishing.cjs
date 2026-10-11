function validateReleaseTag(tag, metadata)
{
    const version = tag?.startsWith('v') && !tag.includes('+') ? tag.slice(1) : null;
    // metadata-action emits the unprefixed tag only when its SemVer parser accepts it.
    if (!version || !metadata.tags.some(image => image.endsWith(`:${version}`)))
    {
        throw new Error('Release tags must use vMAJOR.MINOR.PATCH, optionally followed by a valid prerelease suffix.');
    }
}

async function promoteLatest({ github, context, core, exec, image, digest, manual })
{
    if (context.eventName === 'release')
    {
        const release = context.payload.release;
        if (release.prerelease || release.tag_name.includes('-'))
        {
            core.info('Prereleases do not update latest.');
            return false;
        }

        const { data: latest } = await github.rest.releases.getLatestRelease(context.repo);
        if (latest.tag_name !== release.tag_name)
        {
            core.info(`Skipping latest promotion: ${release.tag_name} is superseded by ${latest.tag_name}.`);
            return false;
        }
    }
    else if (context.eventName !== 'workflow_dispatch' || !manual)
    {
        core.info('No latest promotion requested.');
        return false;
    }

    if (!image || !digest)
    {
        throw new Error('Latest promotion requires the successfully built image and digest.');
    }

    const repository = image.slice(0, image.lastIndexOf(':'));
    await exec.exec('docker', [
        'buildx', 'imagetools', 'create', '--prefer-index=false',
        '--tag', `${repository}:latest`, `${image}@${digest}`
    ]);
    return true;
}

module.exports = { validateReleaseTag, promoteLatest };
