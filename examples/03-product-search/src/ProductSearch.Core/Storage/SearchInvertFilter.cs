using ProductSearch.Core.Data;
using ProductSearch.Shared.Dtos;
using ZVec.NET;
using ZVec.NET.Query;

namespace ProductSearch.Core.Storage;

public static class SearchInvertFilter
{
    public static string? BuildZVecFilter(SearchRequestDto request)
    {
        if (!request.UseInvertFilter)
            return null;

        var builder = ZVecFilterBuilder.Create();
        var has = false;
        if (!string.IsNullOrWhiteSpace(request.Gender))
        {
            builder.Where("Gender", ZVecCompareOp.Eq, request.Gender);
            has = true;
        }

        if (!string.IsNullOrWhiteSpace(request.BaseColour))
        {
            if (has) builder.And(f => f.Where("BaseColour", ZVecCompareOp.Eq, request.BaseColour));
            else { builder.Where("BaseColour", ZVecCompareOp.Eq, request.BaseColour); has = true; }
        }

        if (!string.IsNullOrWhiteSpace(request.Season))
        {
            if (has) builder.And(f => f.Where("Season", ZVecCompareOp.Eq, request.Season));
            else { builder.Where("Season", ZVecCompareOp.Eq, request.Season); has = true; }
        }

        if (!string.IsNullOrWhiteSpace(request.Usage))
        {
            if (has) builder.And(f => f.Where("Usage", ZVecCompareOp.Eq, request.Usage));
            else { builder.Where("Usage", ZVecCompareOp.Eq, request.Usage); has = true; }
        }

        if (!string.IsNullOrWhiteSpace(request.MasterCategory))
        {
            if (has) builder.And(f => f.Where("MasterCategory", ZVecCompareOp.Eq, request.MasterCategory));
            else builder.Where("MasterCategory", ZVecCompareOp.Eq, request.MasterCategory);
        }

        return has ? builder.Build() : null;
    }

    public static IQueryable<ProductEntity> ApplyPostgres(IQueryable<ProductEntity> q, SearchRequestDto request)
    {
        if (!request.UseInvertFilter)
            return q;

        if (!string.IsNullOrWhiteSpace(request.Gender))
            q = q.Where(p => p.Gender == request.Gender);
        if (!string.IsNullOrWhiteSpace(request.BaseColour))
            q = q.Where(p => p.BaseColour == request.BaseColour);
        if (!string.IsNullOrWhiteSpace(request.Season))
            q = q.Where(p => p.Season == request.Season);
        if (!string.IsNullOrWhiteSpace(request.Usage))
            q = q.Where(p => p.Usage == request.Usage);
        if (!string.IsNullOrWhiteSpace(request.MasterCategory))
            q = q.Where(p => p.MasterCategory == request.MasterCategory);

        return q;
    }
}
