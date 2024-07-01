#region License

/*
 * All content copyright Marko Lahma, unless otherwise indicated. All rights reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy
 * of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 *
 */

#endregion

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Quartz.Impl.Matchers;
using Quartz.Logging;
using Quartz.Spi;

namespace Quartz.Simpl;

/// <summary>
/// This class implements a <see cref="IJobStore" /> that
/// utilizes RAM as its storage device.
/// <para>
/// As you should know, the ramification of this is that access is extremely
/// fast, but the data is completely volatile - therefore this <see cref="IJobStore" />
/// should not be used if true persistence between program shutdowns is
/// required.
/// </para>
/// </summary>
/// <author>James House</author>
/// <author>Sharada Jambula</author>
/// <author>Marko Lahma (.NET)</author>
public class RAMJobStore : IJobStore
{
    private readonly SemaphoreSlim lockObject = new(initialCount: 1, maxCount: 1);

    private readonly ConcurrentDictionary<JobKey, JobWrapper> jobsByKey = [];
    private readonly ConcurrentDictionary<TriggerKey, TriggerWrapper> triggersByKey = new();
    private readonly Dictionary<string, Dictionary<JobKey, JobWrapper>> jobsByGroup = [];
    private readonly Dictionary<string, Dictionary<TriggerKey, TriggerWrapper>> triggersByGroup = [];
    private readonly SortedSet<TriggerWrapper> timeTriggers = new(new TriggerWrapperComparator());
    private readonly Dictionary<string, ICalendar> calendarsByName = [];
    private readonly Dictionary<JobKey, List<TriggerWrapper>> triggersByJob = [];
    private readonly HashSet<string> pausedTriggerGroups = [];
    private readonly HashSet<string> pausedJobGroups = [];
    private readonly HashSet<JobKey> blockedJobs = [];
    private TimeSpan misfireThreshold = TimeSpan.FromSeconds(5);
    private ISchedulerSignaler signaler = null!;
    private TimeProvider timeProvider = TimeProvider.System;
    private readonly ILogger<RAMJobStore> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RAMJobStore"/> class.
    /// </summary>
    public RAMJobStore()
    {
        logger = LogProvider.CreateLogger<RAMJobStore>();
    }

    /// <summary>
    /// Gets or sets the time by which a trigger must have missed its next-fire-time, in order for it to
    /// be considered "misfired" and thus have its misfire instruction applied.
    /// </summary>
    /// <value>
    /// The time by which a trigger must have missed its next-fire-time, in order for it to be considered
    /// "misfired" and thus have its misfire instruction applied. The default is <c>5</c> seconds.
    /// </value>
    /// <exception cref="ArgumentException"><paramref name="value"/> represents less than one millisecond.</exception>
    [TimeSpanParseRule(TimeSpanParseRule.Milliseconds)]
    public virtual TimeSpan MisfireThreshold
    {
        get => misfireThreshold;
        set
        {
            if (value.TotalMilliseconds < 1)
            {
                ThrowHelper.ThrowArgumentException("MisfireThreshold must be larger than 0");
            }
            misfireThreshold = value;
        }
    }

    private static long ftrCtr = TimeProvider.System.GetTimestamp();

    /// <summary>
    /// Gets the fired trigger record id.
    /// </summary>
    /// <returns>The fired trigger record id.</returns>
    protected virtual string GetFiredTriggerRecordId()
    {
        long value = Interlocked.Increment(ref ftrCtr);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Called by the QuartzScheduler before the <see cref="IJobStore" /> is
    /// used, in order to give it a chance to Initialize.
    /// </summary>
    public virtual ValueTask Initialize(ITypeLoadHelper loadHelper, ISchedulerSignaler signaler, CancellationToken cancellationToken = default)
    {
        this.signaler = signaler;
        logger.LogInformation("RAMJobStore initialized.");
        return default;
    }

    /// <summary>
    /// Called by the QuartzScheduler to inform the <see cref="IJobStore" /> that
    /// the scheduler has started.
    /// </summary>
    public virtual ValueTask SchedulerStarted(CancellationToken cancellationToken = default)
    {
        // nothing to do
        return default;
    }

    /// <summary>
    /// Called by the QuartzScheduler to inform the JobStore that
    /// the scheduler has been paused.
    /// </summary>
    public ValueTask SchedulerPaused(CancellationToken cancellationToken = default)
    {
        // nothing to do
        return default;
    }

    /// <summary>
    /// Called by the QuartzScheduler to inform the JobStore that
    /// the scheduler has resumed after being paused.
    /// </summary>
    public ValueTask SchedulerResumed(CancellationToken cancellationToken = default)
    {
        // nothing to do
        return default;
    }

    /// <summary>
    /// Called by the QuartzScheduler to inform the <see cref="IJobStore" /> that
    /// it should free up all of it's resources because the scheduler is
    /// shutting down.
    /// </summary>
    public virtual ValueTask Shutdown(CancellationToken cancellationToken = default)
    {
        return default;
    }

    /// <summary>
    /// Returns whether this instance supports persistence.
    /// </summary>
    /// <value></value>
    /// <returns></returns>
    public virtual bool SupportsPersistence => false;

    /// <summary>
    /// Clears (deletes!) all scheduling data - all <see cref="IJob"/>s, <see cref="ITrigger" />s
    /// <see cref="ICalendar"/>s.
    /// </summary>
    public async ValueTask ClearAllSchedulingData(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // unschedule jobs (delete triggers)
            foreach (string group in new List<string>(triggersByGroup.Keys))
            {
                var keys = await GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals(group), cancellationToken).ConfigureAwait(false);
                foreach (TriggerKey key in keys)
                {
                    await RemoveTrigger(key, cancellationToken).ConfigureAwait(false);
                }
            }

            // delete jobs
            foreach (string group in new List<string>(jobsByGroup.Keys))
            {
                var keys = await GetJobKeys(GroupMatcher<JobKey>.GroupEquals(group), cancellationToken).ConfigureAwait(false);
                foreach (JobKey key in keys)
                {
                    await RemoveJob(key, cancellationToken).ConfigureAwait(false);
                }
            }

            // delete calendars
            foreach (string name in new List<string>(calendarsByName.Keys))
            {
                await RemoveCalendar(name, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Store the given <see cref="IJobDetail" /> and <see cref="ITrigger" />.
    /// </summary>
    /// <param name="job">The <see cref="IJobDetail" /> to be stored.</param>
    /// <param name="trigger">The <see cref="ITrigger" /> to be stored.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    public virtual async ValueTask StoreJobAndTrigger(IJobDetail job, IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        await StoreJob(job, replaceExisting: false, cancellationToken).ConfigureAwait(false);
        await StoreTrigger(trigger, replaceExisting: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true if the given job group is paused.
    /// </summary>
    /// <param name="group">Job group name</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    /// <returns></returns>
    public virtual async ValueTask<bool> IsJobGroupPaused(string group, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return pausedJobGroups.Contains(group);
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Returns true if the given TriggerGroup is paused.
    /// </summary>
    /// <returns></returns>
    public virtual async ValueTask<bool> IsTriggerGroupPaused(string group, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return pausedTriggerGroups.Contains(group);
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Store the given <see cref="IJob" />.
    /// </summary>
    /// <param name="job">The <see cref="IJob" /> to be stored.</param>
    /// <param name="replaceExisting">If <see langword="true" />, any <see cref="IJob" /> existing in the
    ///     <see cref="IJobStore" /> with the same name and group should be
    ///     over-written.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    public virtual async ValueTask StoreJob(IJobDetail job, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobKey = job.Key;

            if (jobsByKey.TryGetValue(jobKey, out var originalJob))
            {
                if (!replaceExisting)
                {
                    ThrowHelper.ThrowObjectAlreadyExistsException(job);
                }

                // update job detail
                originalJob.JobDetail = job.Clone();
            }
            else
            {
                // get job group
                if (!jobsByGroup.TryGetValue(jobKey.Group, out var grpMap))
                {
                    grpMap = new Dictionary<JobKey, JobWrapper>();
                    jobsByGroup[jobKey.Group] = grpMap;
                }

                JobWrapper jw = new JobWrapper(job.Clone());

                // add to jobs by group
                grpMap[jobKey] = jw;
                // add to jobs by FQN map
                jobsByKey[jobKey] = jw;
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Remove (delete) the <see cref="IJob" /> with the given
    /// name, and any <see cref="ITrigger" /> s that reference
    /// it.
    /// </summary>
    /// <returns>
    /// 	<see langword="true" /> if a <see cref="IJob" /> with the given name and
    /// group was found and removed from the store.
    /// </returns>
    public virtual async ValueTask<bool> RemoveJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool found = false;
            var triggersForJob = GetTriggersForJobNoLock(jobKey);
            foreach (IOperableTrigger trigger in triggersForJob)
            {
                await RemoveTrigger(trigger.Key, cancellationToken).ConfigureAwait(false);
                found = true;
            }

            found = jobsByKey.TryRemove(jobKey, out _) || found;

            if (found)
            {
                if (jobsByGroup.TryGetValue(jobKey.Group, out var grpMap))
                {
                    if (grpMap.Remove(jobKey) && grpMap.Count == 0)
                    {
                        jobsByGroup.Remove(jobKey.Group);
                    }
                }
            }

            return found;
        }
        finally
        {
            lockObject.Release();
        }
    }

    public async ValueTask<bool> RemoveJobs(IReadOnlyCollection<JobKey> jobKeys, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool allFound = true;
            foreach (JobKey key in jobKeys)
            {
                allFound = await RemoveJob(key, cancellationToken).ConfigureAwait(false) && allFound;
            }

            return allFound;
        }
        finally
        {
            lockObject.Release();
        }
    }

    public async ValueTask<bool> RemoveTriggers(IReadOnlyCollection<TriggerKey> triggerKeys, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool allFound = true;
            foreach (TriggerKey key in triggerKeys)
            {
                allFound = await RemoveTrigger(key, cancellationToken).ConfigureAwait(false) && allFound;
            }

            return allFound;
        }
        finally
        {
            lockObject.Release();
        }
    }

    public async ValueTask StoreJobsAndTriggers(IReadOnlyDictionary<IJobDetail, IReadOnlyCollection<ITrigger>> triggersAndJobs, bool replace, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // make sure there are no collisions...
            if (!replace)
            {
                foreach (var triggersByJob in triggersAndJobs)
                {
                    var job = triggersByJob.Key;

                    if (jobsByKey.ContainsKey(job.Key))
                    {
                        ThrowHelper.ThrowObjectAlreadyExistsException(job);
                    }

                    foreach (ITrigger trigger in triggersByJob.Value)
                    {
                        if (triggersByKey.ContainsKey(trigger.Key))
                        {
                            ThrowHelper.ThrowObjectAlreadyExistsException(trigger);
                        }
                    }
                }
            }

            // do bulk add...
            foreach (var triggersByJob in triggersAndJobs)
            {
                await StoreJob(triggersByJob.Key, replaceExisting: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (ITrigger trigger in triggersByJob.Value)
                {
                    await StoreTrigger((IOperableTrigger) trigger, replaceExisting: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Remove (delete) the <see cref="ITrigger" /> with the
    /// given name.
    /// </summary>
    /// <returns>
    /// 	<see langword="true" /> if a <see cref="ITrigger" /> with the given
    /// name and group was found and removed from the store.
    /// </returns>
    public virtual ValueTask<bool> RemoveTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        return RemoveTrigger(triggerKey, removeOrphanedJob: true);
    }

    /// <summary>
    /// Store the given <see cref="ITrigger" />.
    /// </summary>
    /// <param name="trigger">The <see cref="ITrigger" /> to be stored.</param>
    /// <param name="replaceExisting">If <see langword="true" />, any <see cref="ITrigger" /> existing in
    ///     the <see cref="IJobStore" /> with the same name and group should
    ///     be over-written.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    public virtual async ValueTask StoreTrigger(IOperableTrigger trigger, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TriggerWrapper tw = new TriggerWrapper((IOperableTrigger) trigger.Clone());
            if (triggersByKey.ContainsKey(tw.TriggerKey))
            {
                if (!replaceExisting)
                {
                    ThrowHelper.ThrowObjectAlreadyExistsException(trigger);
                }

                // don't delete orphaned job, this trigger has the job anyways
                await RemoveTrigger(tw.TriggerKey, removeOrphanedJob: false).ConfigureAwait(false);
            }

            if (!jobsByKey.ContainsKey(tw.JobKey))
            {
                ThrowHelper.ThrowJobPersistenceException($"The job ({tw.JobKey}) referenced by the trigger does not exist.");
            }

            // add to triggers by job
            if (!triggersByJob.TryGetValue(tw.JobKey, out var jobList))
            {
                jobList = new List<TriggerWrapper>(1);
                triggersByJob.Add(tw.JobKey, jobList);
            }

            jobList.Add(tw);

            // add to triggers by group
            if (!triggersByGroup.TryGetValue(tw.TriggerKey.Group, out var grpMap))
            {
                grpMap = new Dictionary<TriggerKey, TriggerWrapper>();
                triggersByGroup[tw.TriggerKey.Group] = grpMap;
            }

            grpMap[tw.TriggerKey] = tw;
            // add to triggers by FQN map
            triggersByKey[tw.TriggerKey] = tw;

            if (pausedTriggerGroups.Contains(tw.TriggerKey.Group) || pausedJobGroups.Contains(tw.JobKey.Group))
            {
                tw.state = InternalTriggerState.Paused;
                if (blockedJobs.Contains(tw.JobKey))
                {
                    tw.state = InternalTriggerState.PausedAndBlocked;
                }
            }
            else if (blockedJobs.Contains(tw.JobKey))
            {
                tw.state = InternalTriggerState.Blocked;
            }
            else
            {
                timeTriggers.Add(tw);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Remove (delete) the <see cref="ITrigger" /> with the
    /// given name.
    ///
    /// </summary>
    /// <returns>
    /// 	<see langword="true" /> if a <see cref="ITrigger" /> with the given
    /// name and group was found and removed from the store.
    /// </returns>
    /// <param name="key">The <see cref="ITrigger" /> to be removed.</param>
    /// <param name="removeOrphanedJob">Whether to delete orphaned job details from scheduler if job becomes orphaned from removing the trigger.</param>
    protected virtual async ValueTask<bool> RemoveTrigger(TriggerKey key, bool removeOrphanedJob)
    {
        await lockObject.WaitAsync().ConfigureAwait(false);
        try
        {
            // remove from triggers by FQN map
            var found = triggersByKey.TryRemove(key, out var tw);
            if (tw != null)
            {
                // remove from triggers by group
                if (triggersByGroup.TryGetValue(key.Group, out var grpMap))
                {
                    if (grpMap.Remove(key) && grpMap.Count == 0)
                    {
                        triggersByGroup.Remove(key.Group);
                    }
                }

                //remove from triggers by job
                if (triggersByJob.TryGetValue(tw.JobKey, out var jobList))
                {
                    if (jobList.Remove(tw) && jobList.Count == 0)
                    {
                        triggersByJob.Remove(tw.JobKey);
                    }
                }

                timeTriggers.Remove(tw);

                if (removeOrphanedJob)
                {
                    JobWrapper jw = jobsByKey[tw.JobKey];
                    var trigs = GetTriggersForJobNoLock(tw.JobKey);
                    if (trigs.Count == 0 && !jw.JobDetail.Durable && await RemoveJob(jw.Key).ConfigureAwait(false))
                    {
                        signaler.NotifySchedulerListenersJobDeleted(jw.Key).ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                }
            }

            return found;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Replaces the trigger.
    /// </summary>
    /// <param name="triggerKey">The <see cref="TriggerKey"/> of the <see cref="ITrigger" /> to be replaced.</param>
    /// <param name="trigger">The new trigger.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    public virtual async ValueTask<bool> ReplaceTrigger(TriggerKey triggerKey, IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        bool found;

        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // remove from triggers by FQN map
            triggersByKey.TryRemove(triggerKey, out var tw);
            found = tw != null;

            if (found)
            {
                if (!tw!.JobKey.Equals(trigger.JobKey))
                {
                    ThrowHelper.ThrowJobPersistenceException("New trigger is not related to the same job as the old trigger.");
                }

                // remove from triggers by group
                if (triggersByGroup.TryGetValue(triggerKey.Group, out var grpMap))
                {
                    if (grpMap.Remove(triggerKey) && grpMap.Count == 0)
                    {
                        triggersByGroup.Remove(triggerKey.Group);
                    }
                }

                // remove from triggers by job
                if (triggersByJob.TryGetValue(tw.JobKey, out var jobList))
                {
                    if (jobList.Remove(tw) && jobList.Count == 0)
                    {
                        triggersByJob.Remove(tw.JobKey);
                    }
                }

                timeTriggers.Remove(tw);

                try
                {
                    await StoreTrigger(trigger, replaceExisting: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JobPersistenceException)
                {
                    // put previous trigger back...
                    await StoreTrigger(tw.Trigger, replaceExisting: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            lockObject.Release();
        }
        return found;
    }

    /// <summary>
    /// Retrieve the <see cref="IJobDetail" /> for the given
    /// <see cref="IJob" />.
    /// </summary>
    /// <returns>
    /// The desired <see cref="IJob" />, or null if there is no match.
    /// </returns>
    public virtual ValueTask<IJobDetail?> RetrieveJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        // backing store is concurrent dictionary
        jobsByKey.TryGetValue(jobKey, out JobWrapper? jw);
        var job = jw?.JobDetail.Clone();
        return new ValueTask<IJobDetail?>(job);
    }

    /// <summary>
    /// Retrieve the given <see cref="ITrigger" />.
    /// </summary>
    /// <returns>
    /// The desired <see cref="ITrigger" />, or null if there is no match.
    /// </returns>
    public virtual ValueTask<IOperableTrigger?> RetrieveTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        // backing field is concurrent dictionary
        triggersByKey.TryGetValue(triggerKey, out var tw);
        var trigger = (IOperableTrigger?) tw?.Trigger.Clone();
        return new ValueTask<IOperableTrigger?>(trigger);
    }

    /// <summary>
    /// Determine whether a <see cref="ICalendar" /> with the given identifier already
    /// exists within the scheduler.
    /// </summary>
    /// <remarks>
    /// </remarks>
    /// <param name="name">the identifier to check for</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    /// <returns>true if a calendar exists with the given identifier</returns>
    public ValueTask<bool> CalendarExists(
        string name,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<bool>(calendarsByName.ContainsKey(name));
    }

    /// <summary>
    /// Determine whether a <see cref="IJob"/> with the given identifier already
    /// exists within the scheduler.
    /// </summary>
    /// <param name="jobKey">the identifier to check for</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    /// <returns>true if a Job exists with the given identifier</returns>
    public ValueTask<bool> CheckExists(
        JobKey jobKey,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<bool>(jobsByKey.ContainsKey(jobKey));
    }

    /// <summary>
    /// Determine whether a <see cref="ITrigger" /> with the given identifier already
    /// exists within the scheduler.
    /// </summary>
    /// <param name="triggerKey">triggerKey the identifier to check for</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    /// <returns>true if a Trigger exists with the given identifier</returns>
    public ValueTask<bool> CheckExists(
        TriggerKey triggerKey,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<bool>(triggersByKey.ContainsKey(triggerKey));
    }

    /// <summary>
    /// Get the current state of the identified <see cref="ITrigger" />.
    /// </summary>
    /// <seealso cref="TriggerState.Normal" />
    /// <seealso cref="TriggerState.Paused" />
    /// <seealso cref="TriggerState.Complete" />
    /// <seealso cref="TriggerState.Error" />
    /// <seealso cref="TriggerState.Blocked" />
    /// <seealso cref="TriggerState.None"/>
    public virtual ValueTask<TriggerState> GetTriggerState(
        TriggerKey triggerKey,
        CancellationToken cancellationToken = default)
    {
        triggersByKey.TryGetValue(triggerKey, out var tw);

        if (tw == null)
        {
            return new ValueTask<TriggerState>(TriggerState.None);
        }
        if (tw.state == InternalTriggerState.Complete)
        {
            return new ValueTask<TriggerState>(TriggerState.Complete);
        }
        if (tw.state == InternalTriggerState.Paused)
        {
            return new ValueTask<TriggerState>(TriggerState.Paused);
        }
        if (tw.state == InternalTriggerState.PausedAndBlocked)
        {
            return new ValueTask<TriggerState>(TriggerState.Paused);
        }
        if (tw.state == InternalTriggerState.Blocked)
        {
            return new ValueTask<TriggerState>(TriggerState.Blocked);
        }
        if (tw.state == InternalTriggerState.Error)
        {
            return new ValueTask<TriggerState>(TriggerState.Error);
        }
        return new ValueTask<TriggerState>(TriggerState.Normal);
    }

    public async ValueTask ResetTriggerFromErrorState(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // does the trigger exist?
            if (!triggersByKey.TryGetValue(triggerKey, out var tw) || tw.Trigger == null)
            {
                return;
            }

            // is the trigger in error state?
            if (tw.state != InternalTriggerState.Error)
            {
                return;
            }

            if (pausedTriggerGroups.Contains(triggerKey.Group))
            {
                tw.state = InternalTriggerState.Paused;
            }
            else
            {
                tw.state = InternalTriggerState.Waiting;
                timeTriggers.Add(tw);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Store the given <see cref="ICalendar" />.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="calendar">The <see cref="ICalendar" /> to be stored.</param>
    /// <param name="replaceExisting">If <see langword="true" />, any <see cref="ICalendar" /> existing
    /// in the <see cref="IJobStore" /> with the same name and group
    /// should be over-written.</param>
    /// <param name="updateTriggers">If <see langword="true" />, any <see cref="ITrigger" />s existing
    /// in the <see cref="IJobStore" /> that reference an existing
    /// Calendar with the same name with have their next fire time
    /// re-computed with the new <see cref="ICalendar" />.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    public virtual async ValueTask StoreCalendar(
        string name,
        ICalendar calendar,
        bool replaceExisting,
        bool updateTriggers,
        CancellationToken cancellationToken = default)
    {
        calendar = calendar.Clone();

        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            calendarsByName.TryGetValue(name, out var obj);

            if (obj != null && !replaceExisting)
            {
                ThrowHelper.ThrowObjectAlreadyExistsException($"Calendar with name '{name}' already exists.");
            }

            if (obj != null)
            {
                calendarsByName.Remove(name);
            }

            calendarsByName[name] = calendar;

            if (obj != null && updateTriggers)
            {
                await foreach (TriggerWrapper tw in GetTriggerWrappersForCalendar(name).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    bool removed = timeTriggers.Remove(tw);

                    tw.Trigger.UpdateWithNewCalendar(calendar, MisfireThreshold);

                    if (removed)
                    {
                        timeTriggers.Add(tw);
                    }
                }
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Remove (delete) the <see cref="ICalendar" /> with the
    /// given name.
    /// <para>
    /// If removal of the <see cref="ICalendar" /> would result in
    /// <see cref="ITrigger" />s pointing to non-existent calendars, then a
    /// <see cref="JobPersistenceException" /> will be thrown.</para>
    /// </summary>
    /// <param name="name">The name of the <see cref="ICalendar" /> to be removed.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    /// <returns>
    /// 	<see langword="true" /> if a <see cref="ICalendar" /> with the given name
    /// was found and removed from the store.
    /// </returns>
    public virtual async ValueTask<bool> RemoveCalendar(string name, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int numRefs = 0;
            foreach (TriggerWrapper triggerWrapper in triggersByKey.Values)
            {
                IOperableTrigger trigg = triggerWrapper.Trigger;
                if (trigg.CalendarName != null && trigg.CalendarName.Equals(name))
                {
                    numRefs++;
                }
            }

            if (numRefs > 0)
            {
                ThrowHelper.ThrowJobPersistenceException("Calender cannot be removed if it referenced by a Trigger!");
            }

            return calendarsByName.Remove(name);
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Retrieve the given <see cref="ITrigger" />.
    /// </summary>
    /// <param name="name">The name of the <see cref="ICalendar" /> to be retrieved.</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    /// <returns>
    /// The desired <see cref="ICalendar" />, or null if there is no match.
    /// </returns>
    public virtual ValueTask<ICalendar?> RetrieveCalendar(string name, CancellationToken cancellationToken = default)
    {
        calendarsByName.TryGetValue(name, out var calendar);
        calendar = calendar?.Clone();
        return new ValueTask<ICalendar?>(calendar);
    }

    /// <summary>
    /// Get the number of <see cref="IJobDetail" /> s that are
    /// stored in the <see cref="IJobStore" />.
    /// </summary>
    public virtual ValueTask<int> GetNumberOfJobs(CancellationToken cancellationToken = default)
    {
        // jobsByKey is a concurrent dictionary
        return new ValueTask<int>(jobsByKey.Count);
    }

    /// <summary>
    /// Get the number of <see cref="ITrigger" /> s that are
    /// stored in the <see cref="IJobStore" />.
    /// </summary>
    public virtual async ValueTask<int> GetNumberOfTriggers(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return triggersByKey.Count;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get the number of <see cref="ICalendar" /> s that are
    /// stored in the <see cref="IJobStore" />.
    /// </summary>
    public virtual async ValueTask<int> GetNumberOfCalendars(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return calendarsByName.Count;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get the names of all of the <see cref="IJob" /> s that
    /// match the given group matcher.
    /// </summary>
    public virtual async ValueTask<List<JobKey>> GetJobKeys(GroupMatcher<JobKey> matcher, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<JobKey> outList = [];
            StringOperator op = matcher.CompareWithOperator;
            string compareToValue = matcher.CompareToValue;

            if (StringOperator.Equality.Equals(op))
            {
                if (jobsByGroup.TryGetValue(compareToValue, out var grpMap))
                {
                    foreach (JobWrapper jw in grpMap.Values)
                    {
                        outList.Add(jw.JobDetail.Key);
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, Dictionary<JobKey, JobWrapper>> entry in jobsByGroup)
                {
                    if (op.Evaluate(entry.Key, compareToValue))
                    {
                        foreach (JobWrapper jobWrapper in entry.Value.Values)
                        {
                            outList.Add(jobWrapper.JobDetail.Key);
                        }
                    }
                }
            }

            return [.. outList];
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get the names of all of the <see cref="ICalendar" /> s
    /// in the <see cref="IJobStore" />.
    /// <para>
    /// If there are no ICalendars in the given group name, the result should be
    /// a zero-length array (not <see langword="null" />).
    /// </para>
    /// </summary>
    public virtual async ValueTask<List<string>> GetCalendarNames(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [..calendarsByName.Keys];
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get the names of all of the <see cref="ITrigger" /> s
    /// that have the given group name.
    /// </summary>
    public virtual async ValueTask<List<TriggerKey>> GetTriggerKeys(
        GroupMatcher<TriggerKey> matcher,
        CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<TriggerKey> outList = [];
            StringOperator op = matcher.CompareWithOperator;
            string compareToValue = matcher.CompareToValue;

            if (StringOperator.Equality.Equals(op))
            {
                if (triggersByGroup.TryGetValue(compareToValue, out var grpMap))
                {
                    foreach (TriggerWrapper tw in grpMap.Values)
                    {
                        outList.Add(tw.TriggerKey);
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, Dictionary<TriggerKey, TriggerWrapper>> entry in triggersByGroup)
                {
                    if (op.Evaluate(entry.Key, compareToValue))
                    {
                        foreach (TriggerWrapper triggerWrapper in entry.Value.Values)
                        {
                            outList.Add(triggerWrapper.TriggerKey);
                        }
                    }
                }
            }

            return [..outList];
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get the names of all of the <see cref="IJob" />
    /// groups.
    /// </summary>
    public virtual async ValueTask<List<string>> GetJobGroupNames(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new List<string>(jobsByGroup.Keys);
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get the names of all of the <see cref="ITrigger" /> groups.
    /// </summary>
    public virtual async ValueTask<List<string>> GetTriggerGroupNames(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [..triggersByGroup.Keys];
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Get all the Triggers that are associated to the given Job.
    /// <para>
    /// If there are no matches, a zero-length array should be returned.
    /// </para>
    /// </summary>
    public virtual async ValueTask<List<IOperableTrigger>> GetTriggersForJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return GetTriggersForJobNoLock(jobKey);
        }
        finally
        {
            lockObject.Release();
        }
    }

    private List<IOperableTrigger> GetTriggersForJobNoLock(JobKey jobKey)
    {
        if (triggersByJob.TryGetValue(jobKey, out var jobList))
        {
            var trigList = new List<IOperableTrigger>(jobList.Count);
            for (var i = 0; i < jobList.Count; i++)
            {
                trigList[i] = (IOperableTrigger) jobList[i].Trigger.Clone();
            }
            return trigList;
        }

        return [];
    }

    /// <summary>
    /// Gets the trigger wrappers for job.
    /// </summary>
    /// <returns></returns>
    /// <remarks>
    /// This method should only be executed while holding the instance level lock.
    /// </remarks>
    private List<TriggerWrapper> GetTriggerWrappersForJobInternal(JobKey jobKey)
    {
        if (triggersByJob.TryGetValue(jobKey, out var jobList))
        {
            return jobList;
        }

        return new List<TriggerWrapper>();
    }

    /// <summary>
    /// Gets the trigger wrappers for calendar.
    /// </summary>
    /// <param name="name">Name of the cal.</param>
    /// <returns></returns>
    private async IAsyncEnumerable<TriggerWrapper> GetTriggerWrappersForCalendar(string name)
    {
        await lockObject.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var tw in triggersByKey.Values)
            {
                var tcalName = tw.Trigger.CalendarName;
                if (tcalName != null && tcalName.Equals(name))
                {
                    yield return tw;
                }
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Pause the <see cref="ITrigger" /> with the given name.
    /// </summary>
    public virtual async ValueTask PauseTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // does the trigger exist?
            if (!triggersByKey.TryGetValue(triggerKey, out var tw))
            {
                return;
            }

            // if the trigger is "complete" pausing it does not make sense...
            if (tw.state == InternalTriggerState.Complete)
            {
                return;
            }

            if (tw.state == InternalTriggerState.Blocked)
            {
                tw.state = InternalTriggerState.PausedAndBlocked;
            }
            else
            {
                tw.state = InternalTriggerState.Paused;
            }

            timeTriggers.Remove(tw);
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Pause all of the <see cref="ITrigger" />s in the given group.
    /// <para>
    /// The JobStore should "remember" that the group is paused, and impose the
    /// pause on any new triggers that are added to the group while the group is
    /// paused.
    /// </para>
    /// </summary>
    public virtual async ValueTask<List<string>> PauseTriggers(GroupMatcher<TriggerKey> matcher, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pausedGroups = new HashSet<string>();

            StringOperator op = matcher.CompareWithOperator;
            if (StringOperator.Equality.Equals(op))
            {
                if (pausedTriggerGroups.Add(matcher.CompareToValue))
                {
                    pausedGroups.Add(matcher.CompareToValue);
                }
            }
            else
            {
                foreach (string group in triggersByGroup.Keys)
                {
                    if (op.Evaluate(group, matcher.CompareToValue))
                    {
                        if (pausedTriggerGroups.Add(matcher.CompareToValue))
                        {
                            pausedGroups.Add(group);
                        }
                    }
                }
            }

            foreach (string pausedGroup in pausedGroups)
            {
                var keys = await GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals(pausedGroup), cancellationToken).ConfigureAwait(false);

                foreach (TriggerKey key in keys)
                {
                    await PauseTrigger(key, cancellationToken).ConfigureAwait(false);
                }
            }

            return [..pausedGroups];
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Pause the <see cref="IJobDetail" /> with the given
    /// name - by pausing all of its current <see cref="ITrigger" />s.
    /// </summary>
    public virtual async ValueTask PauseJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var triggersForJob = GetTriggersForJobNoLock(jobKey);
            foreach (IOperableTrigger trigger in triggersForJob)
            {
                await PauseTrigger(trigger.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Pause all of the <see cref="IJobDetail" />s in the
    /// given group - by pausing all of their <see cref="ITrigger" />s.
    /// <para>
    /// The JobStore should "remember" that the group is paused, and impose the
    /// pause on any new jobs that are added to the group while the group is
    /// paused.
    /// </para>
    /// </summary>
    public virtual async ValueTask<List<string>> PauseJobs(GroupMatcher<JobKey> matcher, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<string> pausedGroups = [];
            StringOperator op = matcher.CompareWithOperator;
            if (StringOperator.Equality.Equals(op))
            {
                if (pausedJobGroups.Add(matcher.CompareToValue))
                {
                    pausedGroups.Add(matcher.CompareToValue);
                }
            }
            else
            {
                foreach (string group in jobsByGroup.Keys)
                {
                    if (op.Evaluate(group, matcher.CompareToValue))
                    {
                        if (pausedJobGroups.Add(group))
                        {
                            pausedGroups.Add(group);
                        }
                    }
                }
            }

            foreach (string groupName in pausedGroups)
            {
                foreach (JobKey jobKey in await GetJobKeys(GroupMatcher<JobKey>.GroupEquals(groupName), cancellationToken).ConfigureAwait(false))
                {
                    var triggers = GetTriggersForJobNoLock(jobKey);
                    foreach (IOperableTrigger trigger in triggers)
                    {
                        await PauseTrigger(trigger.Key, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return pausedGroups;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Resume (un-pause) the <see cref="ITrigger" /> with the given key.
    /// </summary>
    /// <remarks>
    /// If the <see cref="ITrigger" /> missed one or more fire-times, then the
    /// <see cref="ITrigger" />'s misfire instruction will be applied.
    /// </remarks>
    public virtual async ValueTask ResumeTrigger(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResumeTriggerNoLock(triggerKey);
        }
        finally
        {
            lockObject.Release();
        }
    }

    private void ResumeTriggerNoLock(TriggerKey triggerKey)
    {
        // does the trigger exist?
        if (!triggersByKey.TryGetValue(triggerKey, out var tw))
        {
            return;
        }

        // if the trigger is not paused resuming it does not make sense...
        if (tw.state != InternalTriggerState.Paused &&
            tw.state != InternalTriggerState.PausedAndBlocked)
        {
            return;
        }

        if (blockedJobs.Contains(tw.JobKey))
        {
            tw.state = InternalTriggerState.Blocked;
        }
        else
        {
            tw.state = InternalTriggerState.Waiting;
        }

        ApplyMisfireNoLock(tw);

        if (tw.state == InternalTriggerState.Waiting)
        {
            timeTriggers.Add(tw);
        }
    }

    /// <summary>
    /// Resume (un-pause) all of the <see cref="ITrigger" />s in the
    /// given group.
    /// <para>
    /// If any <see cref="ITrigger" /> missed one or more fire-times, then the
    /// <see cref="ITrigger" />'s misfire instruction will be applied.
    /// </para>
    /// </summary>
    public virtual async ValueTask<List<string>> ResumeTriggers(GroupMatcher<TriggerKey> matcher, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var groups = new HashSet<string>();
            var keys = await GetTriggerKeys(matcher, cancellationToken).ConfigureAwait(false);

            foreach (TriggerKey triggerKey in keys)
            {
                groups.Add(triggerKey.Group);
                if (triggersByKey.TryGetValue(triggerKey, out var tw))
                {
                    string jobGroup = tw.JobKey.Group;
                    if (pausedJobGroups.Contains(jobGroup))
                    {
                        continue;
                    }
                }

                ResumeTriggerNoLock(triggerKey);
            }

            // Find all matching paused trigger groups, and then remove them.
            StringOperator op = matcher.CompareWithOperator;
            var pausedGroups = new List<string>();
            var matcherGroup = matcher.CompareToValue;
            if (StringOperator.Equality.Equals(op))
            {
                if (pausedTriggerGroups.Contains(matcherGroup))
                {
                    pausedGroups.Add(matcher.CompareToValue);
                }
                else
                {
                    foreach (string group in pausedTriggerGroups)
                    {
                        if (op.Evaluate(group, matcherGroup))
                        {
                            pausedGroups.Add(group);
                        }
                    }
                }

                foreach (string pausedGroup in pausedGroups)
                {
                    pausedTriggerGroups.Remove(pausedGroup);
                }
            }

            return [..groups];
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Resume (un-pause) the <see cref="IJobDetail" /> with
    /// the given name.
    /// <para>
    /// If any of the <see cref="IJob" />'s<see cref="ITrigger" /> s missed one
    /// or more fire-times, then the <see cref="ITrigger" />'s misfire
    /// instruction will be applied.
    /// </para>
    /// </summary>
    public virtual async ValueTask ResumeJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var triggersForJob = GetTriggersForJobNoLock(jobKey);
            foreach (IOperableTrigger trigger in triggersForJob)
            {
                ResumeTriggerNoLock(trigger.Key);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Resume (un-pause) all of the <see cref="IJobDetail" />s
    /// in the given group.
    /// <para>
    /// If any of the <see cref="IJob" /> s had <see cref="ITrigger" /> s that
    /// missed one or more fire-times, then the <see cref="ITrigger" />'s
    /// misfire instruction will be applied.
    /// </para>
    /// </summary>
    public virtual async ValueTask<HashSet<string>> ResumeJobs(GroupMatcher<JobKey> matcher, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resumedGroups = new HashSet<string>();
            var keys = await GetJobKeys(matcher, cancellationToken).ConfigureAwait(false);

            foreach (string pausedJobGroup in pausedJobGroups)
            {
                if (matcher.CompareWithOperator.Evaluate(pausedJobGroup, matcher.CompareToValue))
                {
                    resumedGroups.Add(pausedJobGroup);
                }
            }

            foreach (string resumedGroup in resumedGroups)
            {
                pausedJobGroups.Remove(resumedGroup);
            }

            foreach (JobKey key in keys)
            {
                var triggers = GetTriggersForJobNoLock(key);
                foreach (IOperableTrigger trigger in triggers)
                {
                    ResumeTriggerNoLock(trigger.Key);
                }
            }
            return resumedGroups;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Pause all triggers - equivalent of calling <see cref="PauseTriggers" />
    /// on every group.
    /// <para>
    /// When <see cref="ResumeAll" /> is called (to un-pause), trigger misfire
    /// instructions WILL be applied.
    /// </para>
    /// </summary>
    /// <seealso cref="ResumeAll(CancellationToken)" />
    public virtual async ValueTask PauseAll(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string groupName in triggersByGroup.Keys)
            {
                await PauseTriggers(GroupMatcher<TriggerKey>.GroupEquals(groupName), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Resume (un-pause) all triggers - equivalent of calling <see cref="ResumeTriggers" />
    /// on every trigger group and setting all job groups unpaused />.
    /// <para>
    /// If any <see cref="ITrigger" /> missed one or more fire-times, then the
    /// <see cref="ITrigger" />'s misfire instruction will be applied.
    /// </para>
    /// </summary>
    /// <seealso cref="PauseAll(CancellationToken)" />
    public virtual async ValueTask ResumeAll(CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // TODO need a match all here!
            pausedJobGroups.Clear();

            foreach (string groupName in triggersByGroup.Keys)
            {
                await ResumeTriggers(GroupMatcher<TriggerKey>.GroupEquals(groupName), cancellationToken).ConfigureAwait(false);
            }

            // make sure we don't have anything left in groups
            pausedTriggerGroups.Clear();
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Applies the misfire.
    /// </summary>
    /// <param name="tw">The trigger wrapper.</param>
    /// <returns>
    /// <see langword="true"/> if the next fire time of the trigger was updated from either
    /// one value to another, or from a given value to <see langword="null"/>; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    private bool ApplyMisfireNoLock(TriggerWrapper tw)
    {
        if (tw.Trigger.MisfireInstruction == MisfireInstruction.IgnoreMisfirePolicy)
        {
            return false;
        }

        DateTimeOffset misfireTime = timeProvider.GetUtcNow();
        if (MisfireThreshold > TimeSpan.Zero)
        {
            misfireTime = misfireTime.AddTicks(-1 * MisfireThreshold.Ticks);
        }

        DateTimeOffset? tnft = tw.Trigger.GetNextFireTimeUtc();
        if (!tnft.HasValue || tnft.GetValueOrDefault() > misfireTime)
        {
            return false;
        }

        ICalendar? cal = null;
        if (tw.Trigger.CalendarName != null)
        {
            calendarsByName.TryGetValue(tw.Trigger.CalendarName, out cal);
        }

        signaler.NotifyTriggerListenersMisfired(tw.Trigger.Clone()).ConfigureAwait(false).GetAwaiter().GetResult();
        tw.Trigger.UpdateAfterMisfire(cal);

        var updatedTnft = tw.Trigger.GetNextFireTimeUtc();
        if (!updatedTnft.HasValue)
        {
            tw.state = InternalTriggerState.Complete;
            signaler.NotifySchedulerListenersFinalized(tw.Trigger).ConfigureAwait(false).GetAwaiter().GetResult();

            // We do not remove the trigger that we applied the misfire for (since its next fire time has been
            // updated). Instead we remove a trigger with the same trigger key, but with no next fire time set.
            timeTriggers.Remove(tw);
        }
        else if (tnft.GetValueOrDefault() == updatedTnft.GetValueOrDefault())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Get a handle to the next trigger to be fired, and mark it as 'reserved'
    /// by the calling scheduler.
    /// </summary>
    /// <seealso cref="ITrigger" />
    public virtual async ValueTask<List<IOperableTrigger>> AcquireNextTriggers(DateTimeOffset noLaterThan, int maxCount, TimeSpan timeWindow, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // return empty list if store has no triggers.
            if (timeTriggers.Count == 0)
            {
                return [];
            }

            var result = new List<IOperableTrigger>();
            var acquiredJobKeysForNoConcurrentExec = new HashSet<JobKey>();
            var excludedTriggers = new HashSet<TriggerWrapper>();
            DateTimeOffset batchEnd = noLaterThan;

            while (true)
            {
                var tw = timeTriggers.Min;
                if (tw == null)
                {
                    break;
                }

                // It would've been more efficient to only remove the trigger if we're really acquiring it, but
                // we need to remove it before we apply the misfire. It not, after having updated the trigger,
                // we'd attempt to remove the trigger with the new next fire time which would no longer match
                // the trigger in the 'timeTriggers' set.
                timeTriggers.Remove(tw);

                // Use a local for the next fire time to reduce number of interface calls.
                var tnft = tw.Trigger.GetNextFireTimeUtc();

                // When the trigger is not scheduled to fire, continue with the next trigger.
                if (!tnft.HasValue)
                {
                    continue;
                }

                if (ApplyMisfireNoLock(tw))
                {
                    // If - after applying the misfire policy - the trigger is still scheduled to fire, we'll
                    // add it back to the set of triggers. We cannot use the "cached" next fire time here as
                    // it has been updated in ApplyMisfire(TriggerWrapper tw).
                    if (tw.Trigger.GetNextFireTimeUtc() != null)
                    {
                        timeTriggers.Add(tw);
                    }

                    continue;
                }

                // The first trigger that is scheduled to fire after the window for the current batch completes
                // the current batch.
                if (tnft.GetValueOrDefault() > batchEnd)
                {
                    // Since we removed the trigger from 'timeTriggers' earlier, we now need to add it back.
                    timeTriggers.Add(tw);
                    break;
                }

                JobKey jobKey = tw.JobKey;
                IJobDetail job = jobsByKey[jobKey].JobDetail;

                // If trigger's job disallows concurrent execution and the job was already added to the result,
                // then we'll add the trigger to the list of excluded triggers (which we'll add back to the set
                // of time triggers after we've completed the current batch) and skip the trigger.
                if (job.ConcurrentExecutionDisallowed)
                {
                    if (!acquiredJobKeysForNoConcurrentExec.Add(jobKey))
                    {
                        excludedTriggers.Add(tw);
                        continue; // go to next trigger in store.
                    }
                }

                tw.state = InternalTriggerState.Acquired;
                tw.Trigger.FireInstanceId = GetFiredTriggerRecordId();
                IOperableTrigger trig = (IOperableTrigger) tw.Trigger.Clone();

                result.Add(trig);

                if (result.Count == maxCount)
                {
                    break;
                }

                // Use the next fire time of the first acquired trigger to update the maximum next fire
                // time that we accept for this batch. We only perform this update if we want to acquire
                // more than one trigger.
                if (result.Count == 1)
                {
                    var now = timeProvider.GetUtcNow();
                    var nextFireTime = tnft.GetValueOrDefault();
                    var max = now > nextFireTime ? now : nextFireTime;

                    batchEnd = max + timeWindow;
                }
            }

            // If we did excluded triggers to prevent ACQUIRE state due to DisallowConcurrentExecution, we need to add them back to store.
            if (excludedTriggers.Count > 0)
            {
                foreach (var excludedTrigger in excludedTriggers)
                {
                    timeTriggers.Add(excludedTrigger);
                }
            }

            return result;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Inform the <see cref="IJobStore" /> that the scheduler no longer plans to
    /// fire the given <see cref="ITrigger" />, that it had previously acquired
    /// (reserved).
    /// </summary>
    public virtual async ValueTask ReleaseAcquiredTrigger(IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (triggersByKey.TryGetValue(trigger.Key, out var tw) && tw.state == InternalTriggerState.Acquired)
            {
                tw.state = InternalTriggerState.Waiting;
                timeTriggers.Add(tw);
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Inform the <see cref="IJobStore" /> that the scheduler is now firing the
    /// given <see cref="ITrigger" /> (executing its associated <see cref="IJob" />),
    /// that it had previously acquired (reserved).
    /// </summary>
    public virtual async ValueTask<List<TriggerFiredResult>> TriggersFired(IReadOnlyCollection<IOperableTrigger> triggers, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TriggerFiredResult> results = [];

            foreach (IOperableTrigger trigger in triggers)
            {
                // was the trigger deleted since being acquired?
                if (!triggersByKey.TryGetValue(trigger.Key, out var tw))
                {
                    continue;
                }

                // was the trigger completed, paused, blocked, etc. since being acquired?
                if (tw.state != InternalTriggerState.Acquired)
                {
                    continue;
                }

                ICalendar? cal = null;
                if (tw.Trigger.CalendarName != null)
                {
                    calendarsByName.TryGetValue(tw.Trigger.CalendarName, out cal);
                    if (cal == null)
                    {
                        continue;
                    }
                }

                DateTimeOffset? prevFireTime = trigger.GetPreviousFireTimeUtc();
                // in case trigger was replaced between acquiring and firing
                timeTriggers.Remove(tw);
                // call triggered on our copy, and the scheduler's copy
                tw.Trigger.Triggered(cal);
                trigger.Triggered(cal);
                //tw.state = TriggerWrapper.STATE_EXECUTING;
                tw.state = InternalTriggerState.Waiting;

                var jobDetail = jobsByKey[trigger.JobKey].JobDetail.Clone();
                TriggerFiredBundle bndle = new TriggerFiredBundle(
                    jobDetail,
                    trigger,
                    cal,
                    jobIsRecovering: false,
                    timeProvider.GetUtcNow(),
                    trigger.GetPreviousFireTimeUtc(),
                    prevFireTime,
                    trigger.GetNextFireTimeUtc());

                IJobDetail job = bndle.JobDetail;

                if (job.ConcurrentExecutionDisallowed)
                {
                    var triggerWrappersForJob = GetTriggerWrappersForJobInternal(job.Key);

                    for (var i = 0; i < triggerWrappersForJob.Count; i++)
                    {
                        var ttw = triggerWrappersForJob[i];

                        if (ttw.state == InternalTriggerState.Waiting)
                        {
                            ttw.state = InternalTriggerState.Blocked;
                        }

                        if (ttw.state == InternalTriggerState.Paused)
                        {
                            ttw.state = InternalTriggerState.PausedAndBlocked;
                        }

                        timeTriggers.Remove(ttw);
                    }

                    blockedJobs.Add(job.Key);
                }
                else if (tw.Trigger.GetNextFireTimeUtc() != null)
                {
                    timeTriggers.Add(tw);
                }

                results.Add(new TriggerFiredResult(bndle));
            }

            return results;
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Inform the <see cref="IJobStore" /> that the scheduler has completed the
    /// firing of the given <see cref="ITrigger" /> (and the execution its
    /// associated <see cref="IJob" />), and that the <see cref="JobDataMap" />
    /// in the given <see cref="IJobDetail" /> should be updated if the <see cref="IJob" />
    /// is stateful.
    /// </summary>
    public virtual async ValueTask TriggeredJobComplete(IOperableTrigger trigger, IJobDetail jobDetail, SchedulerInstruction triggerInstCode, CancellationToken cancellationToken = default)
    {
        await lockObject.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // It's possible that the job is null if:
            //   1- it was deleted during execution
            //   2- RAMJobStore is being used only for volatile jobs / triggers
            //      from the JDBC job store

            if (jobsByKey.TryGetValue(jobDetail.Key, out var jw))
            {
                IJobDetail jd = jw.JobDetail;

                if (jobDetail.PersistJobDataAfterExecution)
                {
                    JobDataMap newData = jobDetail.JobDataMap;
                    newData = (JobDataMap) newData.Clone();
                    newData.ClearDirtyFlag();
                    jd = jd.GetJobBuilder().SetJobData(newData).Build();
                    jw.JobDetail = jd;
                }

                if (jd.ConcurrentExecutionDisallowed)
                {
                    blockedJobs.Remove(jd.Key);

                    var triggerWrappersForJob = GetTriggerWrappersForJobInternal(jd.Key);

                    for (var i = 0; i < triggerWrappersForJob.Count; i++)
                    {
                        var ttw = triggerWrappersForJob[i];

                        if (ttw.state == InternalTriggerState.Blocked)
                        {
                            ttw.state = InternalTriggerState.Waiting;
                            timeTriggers.Add(ttw);
                        }

                        if (ttw.state == InternalTriggerState.PausedAndBlocked)
                        {
                            ttw.state = InternalTriggerState.Paused;
                        }
                    }

                    signaler.SignalSchedulingChange(null, cancellationToken);
                }
            }
            else
            {
                // even if it was deleted, there may be cleanup to do
                blockedJobs.Remove(jobDetail.Key);
            }

            // check for trigger deleted during execution...
            if (triggersByKey.TryGetValue(trigger.Key, out var tw))
            {
                if (triggerInstCode == SchedulerInstruction.DeleteTrigger)
                {
                    logger.LogDebug("Deleting trigger");
                    DateTimeOffset? d = trigger.GetNextFireTimeUtc();
                    if (!d.HasValue)
                    {
                        // double check for possible reschedule within job
                        // execution, which would cancel the need to delete...
                        d = tw.Trigger.GetNextFireTimeUtc();
                        if (!d.HasValue)
                        {
                            await RemoveTrigger(trigger.Key, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            logger.LogDebug("Deleting cancelled - trigger still active");
                        }
                    }
                    else
                    {
                        await RemoveTrigger(trigger.Key, cancellationToken).ConfigureAwait(false);
                        signaler.SignalSchedulingChange(null, cancellationToken);
                    }
                }
                else if (triggerInstCode == SchedulerInstruction.SetTriggerComplete)
                {
                    tw.state = InternalTriggerState.Complete;
                    timeTriggers.Remove(tw);
                    signaler.SignalSchedulingChange(null, cancellationToken);
                }
                else if (triggerInstCode == SchedulerInstruction.SetTriggerError)
                {
                    logger.LogInformation("Trigger {TriggerKey} set to ERROR state.", trigger.Key);
                    tw.state = InternalTriggerState.Error;
                    signaler.SignalSchedulingChange(null, cancellationToken);
                }
                else if (triggerInstCode == SchedulerInstruction.SetAllJobTriggersError)
                {
                    logger.LogInformation("All triggers of Job {JobKey} set to ERROR state.", trigger.JobKey);
                    SetAllTriggersOfJobToState(trigger.JobKey, InternalTriggerState.Error);
                    signaler.SignalSchedulingChange(null, cancellationToken);
                }
                else if (triggerInstCode == SchedulerInstruction.SetAllJobTriggersComplete)
                {
                    SetAllTriggersOfJobToState(trigger.JobKey, InternalTriggerState.Complete);
                    signaler.SignalSchedulingChange(null, cancellationToken);
                }
            }
        }
        finally
        {
            lockObject.Release();
        }
    }

    /// <summary>
    /// Inform the <see cref="IJobStore" /> of the Scheduler instance's Id,
    /// prior to initialize being invoked.
    /// </summary>
    string IJobStore.InstanceId
    {
        set { }
    }

    /// <summary>
    /// Inform the <see cref="IJobStore" /> of the Scheduler instance's name,
    /// prior to initialize being invoked.
    /// </summary>
    string IJobStore.InstanceName
    {
        set { }
    }

    int IJobStore.ThreadPoolSize
    {
        set { }
    }

    TimeProvider IJobStore.TimeProvider
    {
        set => timeProvider = value;
    }

    public long EstimatedTimeToReleaseAndAcquireTrigger => 5;

    public bool Clustered => false;

    public virtual TimeSpan GetAcquireRetryDelay(int failureCount) => TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Sets the state of all triggers of job to specified state.
    /// </summary>
    /// <remarks>
    /// This method should only be executed while holding the instance level lock.
    /// </remarks>
    protected virtual void SetAllTriggersOfJobToState(JobKey jobKey, InternalTriggerState state)
    {
        var triggerWrappersForJob = GetTriggerWrappersForJobInternal(jobKey);

        for (var i = 0; i < triggerWrappersForJob.Count; i++)
        {
            var tw = triggerWrappersForJob[i];

            tw.state = state;
            if (state != InternalTriggerState.Waiting)
            {
                timeTriggers.Remove(tw);
            }
        }
    }

    /// <summary>
    /// Peeks the triggers.
    /// </summary>
    /// <returns></returns>
    internal async ValueTask<string> PeekTriggers()
    {
        StringBuilder str = new StringBuilder();

        await lockObject.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (TriggerWrapper tw in triggersByKey.Values)
            {
                str.Append(tw.Trigger.Key.Name);
                str.Append('/');
            }

            str.Append(" | ");

            foreach (TriggerWrapper tw in timeTriggers)
            {
                str.Append(tw.Trigger.Key.Name);
                str.Append("->");
            }
        }
        finally
        {
            lockObject.Release();
        }

        return str.ToString();
    }

    /// <seealso cref="IJobStore.GetPausedTriggerGroups" />
    public virtual ValueTask<List<string>> GetPausedTriggerGroups(CancellationToken cancellationToken = default)
    {
        return new ValueTask<List<string>>([..pausedTriggerGroups]);
    }
}