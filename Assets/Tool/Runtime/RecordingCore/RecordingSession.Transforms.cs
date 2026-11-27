#if PURRNET_RECORDING_HOST
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace PurrNet.Recording
{
    public sealed partial class RecordingSession
    {
        /// <summary>
        /// From this many entities on, their transforms are read by a job on other threads instead of one by one on
        /// the main thread. Below it, starting a job costs more than it saves.
        /// </summary>
        internal static int transformJobFrom = 256;

        /// <summary>For tests: what the job read is compared with what the main thread reads at the same moment.</summary>
        internal static bool checkTransformJob;

        /// <summary>How many poses of this recording came from the job.</summary>
        public long transformsReadByJob { get; private set; }

        /// <summary>
        /// Poses are rounded before they are compared and written: positions to 1/<see cref="positionGrid"/> of a
        /// unit (1/1024 by default, about a millimetre), rotations to 1/16384 per quaternion component, scales to
        /// 1/8192. A body at rest whose physics jitters by a fraction of a millimetre then writes nothing, and the
        /// rows that do change compress much better: in recordings of a large game most transform rows were such
        /// jitter. The grids are powers of two, so every rounded value is exact in a float. 0 writes poses exactly.
        /// </summary>
        public float positionGrid = DefaultPositionGrid;

        public const float DefaultPositionGrid = 1024f;
        internal const float RotationGrid = 16384f, ScaleGrid = 8192f;

        /// <summary>The power of two nearest to one over <paramref name="precision"/> (in units), or 0 for exact poses.</summary>
        public static float GridFor(float precision) =>
            precision > 0 ? Mathf.Pow(2f, Mathf.Clamp(Mathf.Round(-Mathf.Log(precision, 2f)), -8f, 20f)) : 0f;

        void Snap(ref Vector3 position, ref Quaternion rotation, ref Vector3 scale)
        {
            float grid = positionGrid;
            if (!(grid > 0))
                return;
            position = new Vector3(Snap(position.x, grid), Snap(position.y, grid), Snap(position.z, grid));
            rotation = new Quaternion(Snap(rotation.x, RotationGrid), Snap(rotation.y, RotationGrid),
                Snap(rotation.z, RotationGrid), Snap(rotation.w, RotationGrid));
            scale = new Vector3(Snap(scale.x, ScaleGrid), Snap(scale.y, ScaleGrid), Snap(scale.z, ScaleGrid));
        }

        /// <summary>
        /// Makes the pose relative to the entity's frame (its nearest recorded ancestor) as that was on
        /// <paramref name="tick"/>: read then (<see cref="RecordedEntity.worldTick"/>), or now when
        /// <paramref name="now"/> says this is still that tick on the main thread. Without a frame, or with exact
        /// poses (no grid), the pose stays in the world.
        /// </summary>
        void ToFrame(RecordedEntity entity, ulong tick, ref RecordedTransform pose, bool now)
        {
            pose.relativeTo = 0;
            if (!(positionGrid > 0))
                return;
            var frame = FrameOf(entity);
            if (frame == null)
                return;
            Vector3 at;
            Quaternion turn;
            if (frame.worldTick == tick)
            {
                at = frame.worldPosition;
                turn = frame.worldRotation;
            }
            else if (now && frame.root)
                frame.root.GetPositionAndRotation(out at, out turn);
            else
                return;
            var inverse = Quaternion.Inverse(turn);
            var position = inverse * (pose.position - at);
            // Far from its frame the frame's rounded rotation would move it by more than the grid (1/16384 of a turn
            // is about 2 mm at 16 m): such an entity (under a manager object, say) stays in the world.
            if (position.sqrMagnitude > MaxRelativeDistance * MaxRelativeDistance)
                return;
            pose.position = position;
            pose.rotation = inverse * pose.rotation;
            pose.relativeTo = frame.id;
        }

        /// <summary>How far from its frame an entity's pose may be relative to it, in units.</summary>
        internal const float MaxRelativeDistance = 16f;

        /// <summary>
        /// The nearest recorded entity above this one, while it is recorded; looked for again only when the Unity
        /// parent changes (or the entity it was is gone: entities are reused).
        /// </summary>
        RecordedEntity FrameOf(RecordedEntity entity)
        {
            var root = entity.root;
            if (!root)
                return null;
            var up = root.parent;
            if (!entity.frameKnown || !ReferenceEquals(up, entity.frameParent) ||
                (entity.frame != null && entity.frame.id != entity.frameId))
            {
                entity.frameKnown = true;
                entity.frameParent = up;
                entity.frame = up ? ParentEntity(up, true) : null;
                entity.frameId = entity.frame?.id ?? 0;
            }
            var frame = entity.frame;
            if (frame == null || ReferenceEquals(frame, entity) || frame.transform == null ||
                !_entitiesByGameObject.TryGetValue(frame.gameObjectId, out var current) || !ReferenceEquals(current, frame))
                return null;
            return frame;
        }

        /// <summary>
        /// Rounded to the grid; beyond 2^24 / grid every float already is on it (and NaN stays NaN). Adding 0 turns
        /// -0 into 0, so a value that crosses zero and back is the same bits.
        /// </summary>
        static float Snap(float value, float grid) =>
            Math.Abs(value) < 16777216f / grid ? (float)Math.Round(value * grid) / grid + 0f : value;

        /// <summary>
        /// The job cannot read a transform's world scale. It reads the world matrix, whose axes change length when
        /// the scale does: while they keep the length they had when the scale was last read on the main thread, the
        /// scale is that one. When they change, that tick's scale is worked out from how much they changed, and the
        /// entity is read on the main thread for this many ticks, until its scale has settled.
        /// An entity whose scale changes again as soon as the job has it back (it keeps changing, or it turns under a
        /// parent that is scaled unevenly) stays on the main thread twice as long each time.
        /// </summary>
        const int DirectTicksAfterScaleChange = 20, MostDirectTicks = 2400, SettledTicks = 4;
        const float SameAxis = 1e-5f;

        static Vector3 Axes(in Matrix4x4 m) => new Vector3(
            m.m00 * m.m00 + m.m10 * m.m10 + m.m20 * m.m20,
            m.m01 * m.m01 + m.m11 * m.m11 + m.m21 * m.m21,
            m.m02 * m.m02 + m.m12 * m.m12 + m.m22 * m.m22);

        struct ReadTransformsJob : IJobParallelForTransform
        {
            [WriteOnly] public NativeArray<Vector3> positions;
            [WriteOnly] public NativeArray<Quaternion> rotations;
            /// <summary>
            /// The squared lengths of the world matrix's axes. They change when the world scale does.
            /// </summary>
            [WriteOnly] public NativeArray<Vector3> axes;
            [WriteOnly] public NativeArray<byte> alive;

            public void Execute(int index, TransformAccess transform)
            {
                if (!transform.isValid)
                {
                    alive[index] = 0;
                    return;
                }
                positions[index] = transform.position;
                rotations[index] = transform.rotation;
                axes[index] = Axes(transform.localToWorldMatrix);
                alive[index] = 1;
            }
        }

        TransformAccessArray _jobTransforms;
        readonly List<RecordedEntity> _jobEntities = new List<RecordedEntity>(256);
        NativeArray<Vector3> _jobPositions, _jobAxes;
        NativeArray<Quaternion> _jobRotations;
        NativeArray<byte> _jobAlive;
        JobHandle _jobHandle;
        bool _jobPending;
        ulong _jobTick;
        int _jobCount;
        bool _jobThisTick;
        int _laterThisTick;
        int _laterBytes;
        Vector3[] _checkPositions = new Vector3[0];
        Quaternion[] _checkRotations = new Quaternion[0];

        /// <summary>
        /// Nothing of the job exists until a recording has enough entities for it: a small game pays nothing for it.
        /// </summary>
        bool _jobTransformsOn;

        void AddJobTransform(RecordedEntity entity)
        {
            entity.jobIndex = -1;
            entity.laterTick = ulong.MaxValue;
            entity.hasScaleAxes = false;
            entity.directTicks = 0;
            entity.directSpan = 0;
            entity.jobTicks = 0;
            entity.laterVisible = 0;
            if (!_jobTransformsOn || !entity.root)
                return;
            TakeLaterTransforms();
            if (!_jobTransforms.isCreated)
                _jobTransforms = new TransformAccessArray(Mathf.NextPowerOfTwo(Math.Max(256, _entities.Count * 2)));
            entity.jobIndex = _jobEntities.Count;
            _jobEntities.Add(entity);
            _jobTransforms.Add(entity.root);
        }

        /// <summary>
        /// Whether this tick's transforms are read by the job. Once a recording has had enough entities for it, it
        /// stays in use down to half as many, so a count that hovers around the mark does not switch every tick.
        /// </summary>
        bool UseTransformJob()
        {
            if (!_jobTransformsOn)
            {
                if (_entities.Count < transformJobFrom)
                    return false;
                _jobTransformsOn = true;
                foreach (var entity in _entities)
                    if (entity.jobIndex < 0)
                        AddJobTransform(entity);
            }
            return _jobEntities.Count >= transformJobFrom / 2;
        }

        void RemoveJobTransform(RecordedEntity entity)
        {
            int index = entity.jobIndex;
            if (index < 0)
                return;
            TakeLaterTransforms();
            int last = _jobEntities.Count - 1;
            var moved = _jobEntities[last];
            _jobEntities[index] = moved;
            moved.jobIndex = index;
            _jobEntities.RemoveAt(last);
            _jobTransforms.RemoveAtSwapBack(index);
            entity.jobIndex = -1;
        }

        void DisposeTransformJob()
        {
            if (_jobPending)
                _jobHandle.Complete();
            _jobPending = false;
            _jobTransformsOn = false;
            foreach (var entity in _jobEntities)
                entity.jobIndex = -1;
            _jobEntities.Clear();
            if (_jobTransforms.isCreated) _jobTransforms.Dispose();
            if (_jobPositions.IsCreated) _jobPositions.Dispose();
            if (_jobRotations.IsCreated) _jobRotations.Dispose();
            if (_jobAxes.IsCreated) _jobAxes.Dispose();
            if (_jobAlive.IsCreated) _jobAlive.Dispose();
        }

        /// <summary>
        /// Records the entity's pose for this tick like <see cref="RecordTransform"/>, without the caller needing
        /// it back. With many entities the transform is not read here: a job reads all of them at the end of the
        /// tick, on other threads, and the rows are written when the next tick begins (with this tick's number, so
        /// the file is the same). Whether the entity is visible is still looked up here; only the main thread can.
        /// </summary>
        public void RecordTransformLater(RecordedEntity entity, RecordingSource source, ulong tick)
        {
            if (!_jobThisTick || entity.jobIndex < 0 || entity.directTicks > 0 || !entity.hasScaleAxes)
            {
                RecordTransform(entity, source, tick, out _);
                if (!_jobThisTick || entity.jobIndex < 0)
                    return;
                if (entity.directTicks > 0)
                    entity.directTicks--;
                if (entity.directTicks == 0 && entity.root)
                {
                    entity.scaleAxes = Axes(entity.root.localToWorldMatrix);
                    entity.hasScaleAxes = true;
                }
                return;
            }

            entity.Touch(source, tick);
            if (entity.poseTick == tick)
                return;
            entity.poseTick = tick;
            entity.laterTick = tick;
            entity.laterVisible = VisibleNow(entity);
            _laterThisTick++;
        }

        /// <summary>The tick's rows are in: the job that reads the transforms asked for with it starts.</summary>
        void ReadTransformsLater(ulong tick)
        {
            if (_laterThisTick == 0)
                return;
            _laterThisTick = 0;
            int count = _jobEntities.Count;
            if (!_jobPositions.IsCreated || _jobPositions.Length < count)
            {
                int capacity = Mathf.NextPowerOfTwo(Math.Max(256, count));
                if (_jobPositions.IsCreated)
                {
                    _jobPositions.Dispose();
                    _jobRotations.Dispose();
                    _jobAxes.Dispose();
                    _jobAlive.Dispose();
                }
                _jobPositions = new NativeArray<Vector3>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _jobRotations = new NativeArray<Quaternion>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _jobAxes = new NativeArray<Vector3>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _jobAlive = new NativeArray<byte>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (checkTransformJob)
            {
                if (_checkPositions.Length < count)
                {
                    _checkPositions = new Vector3[_jobPositions.Length];
                    _checkRotations = new Quaternion[_jobPositions.Length];
                }
                for (int i = 0; i < count; i++)
                    if (_jobEntities[i].laterTick == tick && _jobEntities[i].root)
                        _jobEntities[i].root.GetPositionAndRotation(out _checkPositions[i], out _checkRotations[i]);
            }

            _jobHandle = new ReadTransformsJob
            {
                positions = _jobPositions, rotations = _jobRotations, axes = _jobAxes, alive = _jobAlive
            }.ScheduleReadOnly(_jobTransforms, 64);
            JobHandle.ScheduleBatchedJobs();
            _jobPending = true;
            _jobTick = tick;
            _jobCount = count;
        }

        /// <summary>
        /// Writes the rows of the poses the job read, with the tick they were read on. Called before anything that
        /// needs the earlier tick to be complete: the next tick, a chunk being closed, an entity going away.
        /// </summary>
        unsafe void TakeLaterTransforms()
        {
            if (!_jobPending)
                return;
            _jobPending = false;
            _jobHandle.Complete();
            if (_file == null)
                return;

            _stats.Begin(_statPoses);
            int start = _job.log.bytes.length;
            ulong tick = _jobTick;
            // Every pose the job read first: an entity's pose is relative to its frame's on the same tick.
            for (int i = 0; i < _jobCount; i++)
            {
                var read = _jobEntities[i];
                if (read.laterTick != tick || _jobAlive[i] == 0)
                    continue;
                read.worldPosition = _jobPositions[i];
                read.worldRotation = _jobRotations[i];
                read.worldTick = tick;
            }
            for (int i = 0; i < _jobCount; i++)
            {
                var entity = _jobEntities[i];
                if (entity.laterTick != tick || _jobAlive[i] == 0)
                    continue;
                var rows = entity.transform;
                if (rows == null)
                    continue;

                var pose = new RecordedTransform
                {
                    position = _jobPositions[i], rotation = _jobRotations[i], scale = entity.lastPose.scale,
                    visible = entity.laterVisible
                };
                var axes = _jobAxes[i];
                var then = entity.scaleAxes;
                if (DifferentAxis(axes.x, then.x) || DifferentAxis(axes.y, then.y) || DifferentAxis(axes.z, then.z))
                {
                    pose.scale = new Vector3(Rescaled(pose.scale.x, axes.x, then.x), Rescaled(pose.scale.y, axes.y, then.y),
                        Rescaled(pose.scale.z, axes.z, then.z));
                    entity.directSpan = entity.jobTicks < SettledTicks && entity.directSpan > 0
                        ? Math.Min(entity.directSpan * 2, MostDirectTicks)
                        : DirectTicksAfterScaleChange;
                    entity.directTicks = entity.directSpan;
                    entity.hasScaleAxes = false;
                    entity.jobTicks = 0;
                }
                else if (entity.jobTicks < SettledTicks)
                    entity.jobTicks++;
                if (checkTransformJob && (!BitsEqual(pose.position, _checkPositions[i]) || !BitsEqual(pose.rotation, _checkRotations[i])))
                    AddError("transform_job_mismatch", $"entity {entity.id} on tick {tick}: {pose.position:R} {pose.rotation:R} read by the job, {_checkPositions[i]:R} {_checkRotations[i]:R} by the main thread");

                transformsReadByJob++;
                ToFrame(entity, tick, ref pose, false);
                Snap(ref pose.position, ref pose.rotation, ref pose.scale);
                if (rows.slot >= 0 && pose.visible == entity.lastPose.visible && pose.relativeTo == entity.lastPose.relativeTo)
                {
                    fixed (RecordedTransform* last = &entity.lastPose)
                        if (UnsafeUtility.MemCmp(&pose, last, 40) == 0)
                        {
                            rows.aliveTick = tick;
                            continue;
                        }
                }
                entity.lastPose = pose;
                AppendRow(rows, tick, ref pose);
            }
            _laterBytes = _job.log.bytes.length - start;
            _stats.End(_statPoses);
        }

        static bool DifferentAxis(float now, float then) => Math.Abs(now - then) > SameAxis * Math.Max(now, then);

        /// <summary>
        /// An axis that is now this much longer or shorter (the lengths are squared) scales by that much.
        /// </summary>
        static float Rescaled(float scale, float now, float then) =>
            then > 0 ? scale * (float)Math.Sqrt(now / then) : (float)Math.Sqrt(now);

        static unsafe bool BitsEqual(Vector3 a, Vector3 b) => UnsafeUtility.MemCmp(&a, &b, 12) == 0;

        static unsafe bool BitsEqual(Quaternion a, Quaternion b) => UnsafeUtility.MemCmp(&a, &b, 16) == 0;
    }
}
#endif
