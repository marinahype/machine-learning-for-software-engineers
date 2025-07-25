using FormCMS.Activities.Models;
using FormCMS.Cms.Services;
using FormCMS.Core.Descriptors;
using FormCMS.Core.Messaging;
using FormCMS.Infrastructure.Buffers;
using FormCMS.Infrastructure.EventStreaming;
using FormCMS.Infrastructure.RelationDbDao;
using FormCMS.Utils.EnumExt;
using FormCMS.Utils.ResultExt;

namespace FormCMS.Activities.Services;

public class ActivityCollectService(
    ICountBuffer countBuffer,
    IStatusBuffer statusBuffer,
    IStringMessageProducer producer,    
    ActivitySettings settings,
    IIdentityService identityService,
    IEntitySchemaService entitySchemaService,  
    IEntityService entityService,
    IPageResolver pageResolver,
    IContentTagService contentTagService,
    KateQueryExecutor executor,
    IRelationDbDao dao,
    IUserManageService userManager,
    DatabaseMigrator migrator
) : IActivityCollectService
{
    public async Task EnsureActivityTables()
    {
        await migrator.MigrateTable(Models.Activities.TableName, Models.Activities.Columns);
        await dao.CreateIndex(Models.Activities.TableName, Models.Activities.KeyFields, true, CancellationToken.None);

        await migrator.MigrateTable(ActivityCounts.TableName, ActivityCounts.Columns);
        await dao.CreateIndex(ActivityCounts.TableName, ActivityCounts.KeyFields, true, CancellationToken.None);
    }

    public async Task Flush(DateTime? lastFlushTime, CancellationToken ct)
    {
        if (!settings.EnableBuffering)
            return;

        lastFlushTime ??= DateTime.UtcNow.AddMinutes(-1);

        var counts = await countBuffer.GetAfterLastFlush(lastFlushTime.Value);
        var countRecords = counts.Select(pair =>
            (ActivityCounts.Parse(pair.Key) with { Count = pair.Value }).UpsertRecord()).ToArray();
        
        await dao.BatchUpdateOnConflict(ActivityCounts.TableName,  countRecords, ActivityCounts.KeyFields,ct);
        
        //Query title and image 
        var statusList = await statusBuffer.GetAfterLastFlush(lastFlushTime.Value);
        var activities = statusList.Select(pair => Models.Activities.Parse(pair.Key) with { IsActive = pair.Value }).ToArray();
        await UpsertActivities(activities,ct);
    }
    
    public async Task<Dictionary<string, StatusDto>> Get(string cookieUserId,string entityName, long recordId, CancellationToken ct)
    {
        var entity = await entityService.GetEntityAndValidateRecordId(entityName, recordId,ct).Ok();
        var ret = new Dictionary<string, StatusDto>();
        foreach (var pair in await InternalRecord(cookieUserId,entity,entityName, recordId, settings.CommandAutoRecordActivities.ToArray(), ct))
        {
            ret[pair.Key] = new StatusDto(true, pair.Value);
        }

        string[] types = [..settings.CommandToggleActivities, ..settings.CommandRecordActivities];
        var userId = identityService.GetUserAccess()?.Id;
        
        var counts = types.Select(x => 
            new ActivityCount(entityName, recordId, x)).ToArray();

        Dictionary<string, bool>? statusDict = null;
        if (userId is not null)
        {
            var activities = types.Select(x 
                => new Activity(entityName, recordId, x, userId)
            ).ToArray();
            statusDict = settings.EnableBuffering 
                ? await GetBufferStatusDict(activities) 
                : await GetDbStatusDict(activities, ct);
        }

        var countDict = await GetCountDict(entityName,recordId, types,ct);

        foreach (var t in types)
        {
            var isActive = statusDict is not null && statusDict.TryGetValue(t, out var b) && b;
            var count = countDict.TryGetValue(t, out var l) ? l : 0;
            ret[t] = new StatusDto(isActive, count);
        }

        return ret;
    }

    public async Task<Dictionary<string,long>> GetCountDict(string entityName, long recordId,string[] types, CancellationToken ct)
    {
        var counts = types.Select(x => 
            new ActivityCount(entityName, recordId, x)).ToArray();
 
        return settings.EnableBuffering
            ? await GetBufferCountDict(counts)
            : await GetDbCountDict(counts, ct); 
    }

    //why not log visit at page service directly?page service might cache result
    public async Task Visit( string cookieUserId, string url, CancellationToken ct )
    {
        var path = new Uri(url).AbsolutePath.TrimStart('/');
        var page = await pageResolver.GetPage(path, ct);
        await InternalRecord(cookieUserId, null,Constants.PageEntity, page.Id, [Constants.VisitActivityType], ct);
    }

    public async Task<Dictionary<string, long>> Record(
        string cookieUserId,
        string entityName,
        long recordId,
        string[] activityTypes,
        CancellationToken ct
    )
    {

        if (activityTypes.Any(t =>
                !settings.CommandRecordActivities.Contains(t) &&
                !settings.CommandAutoRecordActivities.Contains(t)))
        {
            throw new ResultException("One or more activity types are not supported.");
        }
        var entity = await entityService.GetEntityAndValidateRecordId(entityName, recordId,ct).Ok();

        return await InternalRecord(cookieUserId, entity,entityName, recordId, activityTypes, ct);
    }

    public async Task<long> Toggle(
        string entityName,
        long recordId,
        string activityType,
        bool isActive,
        CancellationToken ct)
    {
        if (!settings.CommandToggleActivities.Contains(activityType))
            throw new ResultException($"Activity type {activityType} is not supported");

        if (identityService.GetUserAccess() is not { Id: var userId })
            throw new ResultException("User is not logged in");

        var entity = await entityService.GetEntityAndValidateRecordId(entityName, recordId, ct).Ok();
 

        var activity = new Activity(entityName, recordId, activityType, userId,isActive);
        var count = new ActivityCount(entityName, recordId, activityType);
        var delta = isActive ? 1 : -1;

        if (settings.EnableBuffering)
        {
            return await statusBuffer.Toggle(activity.Key(), isActive, GetStatus) switch
            {
                true => await countBuffer.Increase(count.Key(), delta, GetCount),
                false => (await countBuffer.Get([count.Key()], GetCount)).FirstOrDefault().Value
            };
        }

        //only update is Active field, to determine if you should increase count
        var changed = await dao.UpdateOnConflict(
            Models.Activities.TableName,
            activity.UpsertRecord(false), 
            Models.Activities.KeyFields, 
            ct);
        var ret= changed switch
        {
            true => await UpdateActivityMetaAndIncrease(),
            false => (await dao.FetchValues<long>(
                    ActivityCounts.TableName,
                    count.Condition(true),
                    null, null,
                    ActivityCounts.CountField,
                    ct))
                .FirstOrDefault().Value
        };
        
        await UpdateScore(entity,[count],ct);
        return ret;

        async Task<long> UpdateActivityMetaAndIncrease()
        {
            if (activity.IsActive)
            {
                var loadedActivities = await LoadMetaData(entity, [activity], ct);
                if (loadedActivities.Length == 0) throw new ResultException("No activities loaded");
                await ProduceActivityMessage(entity, loadedActivities[0], ct);
                
                await dao.UpdateOnConflict(Models.Activities.TableName,
                    loadedActivities[0].UpsertRecord(true), Models.Activities.KeyFields, ct);
            }

            return await dao.Increase(
                ActivityCounts.TableName,
                count.Condition(true),
                ActivityCounts.CountField,
                0,
                delta,
                ct);
        }
    }
  

    private async Task UpdateScore(LoadedEntity entity,ActivityCount[] counts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entity.BookmarkQuery)) return;
        
        foreach (var a in counts)
        {
            await UpdateOneScore(a);
        }

        return;

        async Task UpdateOneScore(ActivityCount count)
        {
            if (!settings.Weights.TryGetValue(count.ActivityType, out var weight))
            {
                return;
            }

            count = count with { ActivityType = Constants.ScoreActivityType };
            if (settings.EnableBuffering)
            {
                await countBuffer.Increase(count.Key(), weight, GetItemScore);
            }
            else
            {
                var timeScore = await GetInitialScoreByPublishedAt();
                await dao.Increase(
                    ActivityCounts.TableName, count.Condition(true),
                    ActivityCounts.CountField,timeScore, weight, ct);
            }

            return;

            async Task<long> GetItemScore(string _)
            {
                var dict = await GetDbCountDict([count], ct);
                if (dict.Count > 0)
                {
                    return dict.First().Value;
                }

                return await GetInitialScoreByPublishedAt() + weight;
            }

            async Task<long> GetInitialScoreByPublishedAt()
            {
                var rec = await executor.Single(entity.PublishedAt(count.RecordId),ct);
                if (rec is null 
                    || !rec.TryGetValue(DefaultAttributeNames.PublishedAt.Camelize(), out var value) 
                    || value is null) throw new ResultException("invalid publish time");
                
                var publishTime = value switch
                {
                    string s => DateTime.Parse(s),
                    DateTime d => d,
                    _ => throw new ResultException("invalid publish time")
                };
                var hoursFromNowToReference = (long)(publishTime - settings.ReferenceDateTime).TotalHours;
                return hoursFromNowToReference * settings.HourBoostWeight;
            }
        }
    }
    private async Task<Dictionary<string, long>> InternalRecord(
        string cookieUserId,
        LoadedEntity? entity,
        string entityName,
        long recordId,
        string[] activityTypes,
        CancellationToken ct
    ){

        var userId = identityService.GetUserAccess()?.Id ?? cookieUserId;
        var activities = activityTypes.Select(x => new Activity(entityName, recordId, x, userId)).ToArray();

        var counts = activityTypes
            .Select(x => new ActivityCount(entityName, recordId, x))
            .ToArray();

        var result = new Dictionary<string, long>();

        if (settings.EnableBuffering)
        {
            await statusBuffer.Set( activities.Select(a => a.Key()).ToArray());
            foreach (var count in counts)
            {
                result[count.ActivityType] = await countBuffer.Increase(count.Key(), 1, GetCount);
            }
        }
        else
        {
            await UpsertActivities(activities,ct);
            foreach (var count in counts)
            {
                result[count.ActivityType] = await dao.Increase(
                    ActivityCounts.TableName, count.Condition(true),
                    ActivityCounts.CountField, 0,1, ct);
            }
        }

        if (entity is not null)
        {
            await UpdateScore(entity,counts,ct);
        }
        return result;
    }
    private async Task<Activity[]> LoadMetaData(LoadedEntity entity, Activity[] activities, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entity.BookmarkQuery))
        {
            return activities;
        }
        
        var ids = activities
            .Where(x=>x.IsActive)
            .Select(x => x.RecordId.ToString())
            .ToArray();
        if (ids.Length == 0) return activities;

        var tags = await contentTagService.GetContentTags( entity, ids, ct);
        
        var dict = tags.ToDictionary(x => x.RecordId);
        return activities.Select(activity=>
        {
            if (dict.TryGetValue(activity.RecordId.ToString(), out var link))
            {
                return activity with
                {
                    Title = link.Title,
                    Url = link.Url,
                    Image = link.Image,
                    Subtitle = link.Subtitle,
                    PublishedAt = link.PublishedAt,
                };
            }
            return activity;

        }).ToArray();
    }

    private async Task ProduceActivityMessage(LoadedEntity entity, Activity activity, CancellationToken ct)
    {
        var targetUserId = await 
            userManager.GetCreatorId(entity.TableName, entity.PrimaryKey, activity.RecordId,ct);
                    
        var msg = new ActivityMessage(
            UserId:  activity.UserId,
            TargetUserId: targetUserId,
            EntityName: entity.Name,
            RecordId: activity.RecordId,
            Activity: activity.ActivityType,
            Operation:CmsOperations.Create,
            Message: activity.Title,
            Url: activity.Url
        );
        
        await producer.Produce(CmsTopics.CmsActivity, msg.ToJson()); 
    }
    private async Task UpsertActivities(Activity[] activities,CancellationToken ct)
    {
        var groups = activities.GroupBy(a => a.EntityName);
        var toUpdate = new List<Record>();
        var entities = await entitySchemaService.AllEntities(ct);
        
        foreach (var group in groups)
        {
            if (group.Key == Constants.PageEntity)
            {
                toUpdate.AddRange(group.Select(x=>x.UpsertRecord(false)));
            }
            else
            {
                var entity = entities.FirstOrDefault(x => x.Name == group.Key);
                if (entity == null) continue;
                var loadedEntity = entity.ToLoadedEntity();
                var loadedActivities = await LoadMetaData(loadedEntity, [..group], ct);
                foreach (var activity in loadedActivities)
                {
                    await ProduceActivityMessage(loadedEntity, activity, ct);
                }
                toUpdate.AddRange(loadedActivities.Select(x => x.UpsertRecord(true)));
            }
        }
        await dao.BatchUpdateOnConflict(
            Models.Activities.TableName,
            toUpdate.ToArray(),
            Models.Activities.KeyFields,
            ct);
    }

    private async Task<Dictionary<string, bool>> GetBufferStatusDict(Activity[] status)
    {
        var keys = status.Select(Models.Activities.Key).ToArray();
        var dict = await statusBuffer.Get(keys, GetStatus);
        var ret = new Dictionary<string, bool>();
        foreach (var (key, value) in dict)
        {
            var activity = Models.Activities.Parse(key);
            ret[activity.ActivityType] = value;
        }
        return ret;
    }
    
    private  async Task<Dictionary<string,bool>> GetDbStatusDict(Activity[] activities, CancellationToken ct)
    {
        var userId = identityService.GetUserAccess()?.Id;
        if (activities.Length == 0 || userId is null) return [];
        
        return await dao.FetchValues<bool>(
            Models.Activities.TableName, 
            activities.First().Condition(false), 
            Models.Activities.TypeField,
            activities.Select(x=>x.ActivityType), 
            Models.Activities.ActiveField,
            ct);
    }

    private async Task<Dictionary<string, long>> GetBufferCountDict(ActivityCount[] counts)
    {
        var dict = await countBuffer.Get(counts.Select(ActivityCounts.Key).ToArray(), GetCount);
        var ret = new Dictionary<string, long>();
        foreach (var (key, value) in dict)
        {
            var ct = ActivityCounts.Parse(key);
            ret[ct.ActivityType] = value;
        }
        return ret;
    }
    
    private async Task<Dictionary<string, long>> GetDbCountDict(ActivityCount[] counts, CancellationToken ct)
    {
        if (counts.Length == 0 ) return [];

        return await dao.FetchValues<long>(
            ActivityCounts.TableName,
            counts.First().Condition(false),
            ActivityCounts.TypeField,
            counts.Select(x => x.ActivityType),
            ActivityCounts.CountField,
            ct);
    }

    private async Task<bool> GetStatus(string key)
    {
        var activity = Models.Activities.Parse(key);
        var res = await dao.FetchValues<bool>(
            Models.Activities.TableName, 
            activity.Condition(true), 
            null,null,Models.Activities.ActiveField);
        return res.Count > 0 && res.First().Value;
    }

    private async Task<long> GetCount(string key)
    {
        var count = ActivityCounts.Parse(key);
        var res = await dao.FetchValues<long>(
            ActivityCounts.TableName, 
            count.Condition(true), 
            null,null,
            ActivityCounts.CountField);
        return res.Count > 0 ? res.First().Value:0;
    }
}