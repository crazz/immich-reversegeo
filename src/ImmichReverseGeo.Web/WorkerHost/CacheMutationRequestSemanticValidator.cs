using System;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Web.WorkerHost;

internal interface IWorkerJobRequestSemanticValidator
{
    bool IsValid(IWorkerJobRequest request);
}

internal sealed class CacheMutationRequestSemanticValidator(
    CountryCodeService countries) : IWorkerJobRequestSemanticValidator
{
    public bool IsValid(IWorkerJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is not CacheMutationRequest cacheMutation)
        {
            return true;
        }

        if (countries.FindByAlpha3(cacheMutation.Iso3) is null)
        {
            return false;
        }

        return cacheMutation.Source switch
        {
            CacheMutationSource.Overture =>
                countries.Iso3ToAlpha2(cacheMutation.Iso3) is { Length: 2 },
            CacheMutationSource.Gadm =>
                GadmCountryCodeMapper.ToGadmCode(cacheMutation.Iso3) is { Length: 3 },
            _ => false
        };
    }
}
