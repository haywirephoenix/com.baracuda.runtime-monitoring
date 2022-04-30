using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Baracuda.Monitoring.Internal.Pooling.Concretions;
using Baracuda.Monitoring.Internal.Profiling;
using Baracuda.Monitoring.Internal.Reflection;
using Baracuda.Monitoring.Internal.Units;
using Baracuda.Threading;

namespace Baracuda.Monitoring.API
{
    public static class MonitoringUnitManager
    {
        #region --- API ---

        /*
         * Target Object Registration   
         */
        
        /// <summary>
        /// Register an object that is monitored during runtime.
        /// </summary>
        public static void RegisterTarget(object target)
        {
            RegisterTargetInternal(target);
        }
        
        /// <summary>
        /// Unregister an object that is monitored during runtime.
        /// </summary>
        public static void UnregisterTarget(object target)
        {
            UnregisterTargetInternal(target);
        }

        /*
         * Getter   
         */        
        
        /// <summary>
        /// Get a list of monitoring units for static targets.
        /// </summary>
        public static IReadOnlyList<MonitorUnit> GetStaticUnits() => staticUnits;
        
        /// <summary>
        /// Get a list of monitoring units for instance targets.
        /// </summary>
        public static IReadOnlyList<MonitorUnit> GetInstanceUnits() => instanceUnits;
        
        #endregion

        //--------------------------------------------------------------------------------------------------------------
        
        #region --- Private Fields ---
        
        private static readonly List<MonitorUnit> staticUnits = new List<MonitorUnit>(30);
        private static readonly List<MonitorUnit> instanceUnits = new List<MonitorUnit>(30);
        
        private static readonly Dictionary<object, MonitorUnit[]> activeInstanceUnits = new Dictionary<object, MonitorUnit[]>();
        
        private static readonly List<object> registeredTargets = new List<object>(300);
        private static bool initialInstanceUnitsCreated = false;
        
        #endregion
        
        //--------------------------------------------------------------------------------------------------------------
        
        #region --- Internal Target Registration ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterTargetInternal(object target)
        {
            registeredTargets.Add(target);
            if (initialInstanceUnitsCreated)
            {
                CreateInstanceUnits(target, target.GetType());
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnregisterTargetInternal(object target)
        {
            DestroyInstanceUnits(target);
            registeredTargets.Remove(target);
        }

        #endregion
        
        #region --- Internal Complete Profiling ---

        internal static async Task CompleteProfilingAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CreateStaticUnits(MonitoringProfiler.StaticProfiles.ToArray());
            await Dispatcher.InvokeAsync(CreateInitialInstanceUnits, ct);
            await Dispatcher.InvokeAsync(() => MonitoringEvents.ProfilingCompletedInternal(staticUnits.ToArray(), instanceUnits.ToArray()), ct);
        }
        
        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Instantiate: Instance Units ---
        
        private static void CreateInitialInstanceUnits()
        {
            for (var i = 0; i < registeredTargets.Count; i++)
            {
                CreateInstanceUnits(registeredTargets[i], registeredTargets[i].GetType());
            }
            initialInstanceUnitsCreated = true;
        }

        private static void CreateInstanceUnits(object target, Type type)
        {
            var validTypes = type.GetBaseTypes(true);
            // create a new array to cache the units instances that will be created. 
            var units = ConcurrentListPool<MonitorUnit>.Get();
            var guids = ConcurrentListPool<MemberInfo>.Get();
            
            for (var i = 0; i < validTypes.Length; i++)
            {
                if(validTypes[i].IsGenericType)
                {
                    continue;
                }

                if (!MonitoringProfiler.InstanceProfiles.TryGetValue(validTypes[i], out var profiles))
                {
                    continue;
                }

                // loop through the profiles and create a new unit for each profile.
                for (var j = 0; j < profiles.Count; j++)
                {
                    if(guids.Contains(profiles[j].MemberInfo))
                    {
                        continue;
                    }

                    guids.Add(profiles[j].MemberInfo);
                        
                    var unit = profiles[j].CreateUnit(target);
                    units.Add(unit);
                    instanceUnits.Add(unit);
                    MonitoringEvents.RaiseUnitCreated(unit);
                }
            }

            // cache the created units in a dictionary that allows access by the units target.
            // this dictionary will be used to dispose the units if the target gets destroyed 
            if (units.Count > 0 && !activeInstanceUnits.ContainsKey(target))
            {
                activeInstanceUnits.Add(target, units.ToArray());
            }
            ConcurrentListPool<MemberInfo>.Release(guids);
            ConcurrentListPool<MonitorUnit>.Release(units);
        }

        #endregion

        #region --- Dispose: Instance Units ---

        private static void DestroyInstanceUnits(object target)
        {
            if (!activeInstanceUnits.TryGetValue(target, out var units))
            {
                return;
            }

            for (var i = 0; i < units.Length; i++)
            {
                units[i].Dispose();
                MonitoringEvents.RaiseUnitDisposed(units[i]);
            }
                
            activeInstanceUnits.Remove(target);
        }

        #endregion
        
        #region --- Instantiate: Static Units ---
        
        private static void CreateStaticUnits(MonitorProfile[] staticProfiles)
        {
            for (var i = 0; i < staticProfiles.Length; i++)
            {
                CreateStaticUnit(staticProfiles[i]);
            }
        }
        
        private static void CreateStaticUnit(MonitorProfile staticProfile)
        {
            staticUnits.Add(staticProfile.CreateUnit(null));
        }

        #endregion
    }
}