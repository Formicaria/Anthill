/* =============================================================================
 *  ANTHILL native compute kernel — public C ABI
 *
 *  This is the C++ half of the v1.8.0 hybrid harness. The managed C# colony
 *  (Queen, ants, memory, API) owns orchestration; this kernel owns the few
 *  numeric hot paths where a native implementation pays off and where we want
 *  deterministic, allocation-free maths:
 *
 *    - pheromone trail reinforcement / decay (called on every model + tool event)
 *    - mission success scoring math
 *    - dependency-graph cycle detection (called whenever a graph is validated)
 *
 *  Everything is exported with C linkage and plain POD parameters so the C#
 *  side can bind to it through P/Invoke without any marshalling surprises.
 *  Keep this header free of C++-only types: it doubles as the contract the
 *  managed NativeKernel wrapper is written against.
 * ========================================================================== */
#ifndef ANTHILL_KERNEL_H
#define ANTHILL_KERNEL_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  define ANTHILL_API __declspec(dllexport)
#else
#  define ANTHILL_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI version so the managed loader can refuse to bind to a mismatched build. */
#define ANTHILL_KERNEL_ABI_VERSION 1

ANTHILL_API int32_t anthill_kernel_abi_version(void);

/*
 * Reinforce or decay a pheromone trail strength.
 *
 *   current_strength  existing trail strength (clamped 0..1 on the way out)
 *   delta             signed reinforcement; positive on success, negative on failure
 *   success           non-zero applies a small additional success bias
 *
 * Returns the new strength, clamped to [0.0, 1.0]. This mirrors the colony
 * intuition that no single event should saturate or fully erase a trail.
 */
ANTHILL_API double anthill_pheromone_update(double current_strength,
                                            double delta,
                                            int32_t success);

/*
 * Apply uniform evaporation to a batch of trail strengths in place.
 * Each value becomes max(floor, value * (1 - rate)). Returns the count touched.
 */
ANTHILL_API int32_t anthill_pheromone_decay_batch(double* strengths,
                                                  int32_t count,
                                                  double rate,
                                                  double floor);

/*
 * Compute a mission success score from aggregate task counts and quality flags.
 * Faithful to the managed PheromoneEngine contract; result clamped to [0,1].
 */
ANTHILL_API double anthill_score_mission(int32_t total_tasks,
                                         int32_t completed_tasks,
                                         int32_t failed_tasks,
                                         int32_t skipped_tasks,
                                         int32_t builder_quality_bonus,
                                         int32_t verifier_verdict);

/*
 * Detect every task that participates in a dependency cycle.
 *
 * The graph is supplied as a flat edge list: edge i runs dep_from[i] -> dep_to[i]
 * meaning "task dep_to depends on task dep_from". Node ids are dense indices
 * [0, node_count). On return, in_cycle[k] is set to 1 for every node that lies
 * on at least one cycle, 0 otherwise. Returns the number of nodes flagged.
 */
ANTHILL_API int32_t anthill_detect_cycles(int32_t node_count,
                                          const int32_t* dep_from,
                                          const int32_t* dep_to,
                                          int32_t edge_count,
                                          uint8_t* in_cycle);

#ifdef __cplusplus
}
#endif

#endif /* ANTHILL_KERNEL_H */
