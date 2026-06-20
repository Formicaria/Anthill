/* =============================================================================
 *  ANTHILL native compute kernel — implementation
 *
 *  Notes for future maintainers:
 *   - No exceptions cross the ABI boundary. Every entry point is noexcept in
 *     spirit; bad input is clamped or treated as a no-op rather than throwing.
 *   - No heap allocation on the pheromone hot paths. The cycle detector does
 *     allocate adjacency vectors, but that runs only during graph validation.
 *   - Behaviour here is kept bit-for-bit aligned with the managed fallbacks in
 *     Anthill.Core so a build without the native library produces identical
 *     numbers (see NativeKernel.cs).
 * ========================================================================== */
#include "anthill_kernel.h"

#include <algorithm>
#include <vector>

namespace {

inline double clamp01(double value) {
    if (value < 0.0) return 0.0;
    if (value > 1.0) return 1.0;
    return value;
}

} // namespace

extern "C" {

int32_t anthill_kernel_abi_version(void) {
    return ANTHILL_KERNEL_ABI_VERSION;
}

double anthill_pheromone_update(double current_strength, double delta, int32_t success) {
    double next = current_strength + delta;
    // A successful event nudges the trail a touch harder so winning paths
    // brighten slightly faster than they fade — the colony's optimism bias.
    if (success != 0) {
        next += 0.002;
    }
    return clamp01(next);
}

int32_t anthill_pheromone_decay_batch(double* strengths, int32_t count, double rate, double floor) {
    if (strengths == nullptr || count <= 0) {
        return 0;
    }
    const double retain = 1.0 - clamp01(rate);
    const double safe_floor = clamp01(floor);
    for (int32_t i = 0; i < count; ++i) {
        double decayed = strengths[i] * retain;
        strengths[i] = decayed < safe_floor ? safe_floor : clamp01(decayed);
    }
    return count;
}

double anthill_score_mission(int32_t total_tasks,
                             int32_t completed_tasks,
                             int32_t failed_tasks,
                             int32_t skipped_tasks,
                             int32_t builder_quality_bonus,
                             int32_t verifier_verdict) {
    if (total_tasks <= 0) {
        return 0.0;
    }
    double score = (static_cast<double>(completed_tasks) / static_cast<double>(total_tasks))
                 - (static_cast<double>(failed_tasks) * 0.25)
                 - (static_cast<double>(skipped_tasks) * 0.05);
    if (builder_quality_bonus != 0) {
        score += 0.10;
    }
    // verifier_verdict: -1 failed, 0 needs_improvement, 1 passed, 2 unknown/none
    if (verifier_verdict == -1) {
        score -= 0.25;
    } else if (verifier_verdict == 0) {
        score -= 0.10;
    } else if (verifier_verdict == 1) {
        score += 0.05;
    }
    // Round to two decimals to match the managed engine exactly.
    double rounded = static_cast<double>(static_cast<long long>((score * 100.0) + (score >= 0 ? 0.5 : -0.5))) / 100.0;
    return clamp01(rounded);
}

int32_t anthill_detect_cycles(int32_t node_count,
                              const int32_t* dep_from,
                              const int32_t* dep_to,
                              int32_t edge_count,
                              uint8_t* in_cycle) {
    if (in_cycle == nullptr || node_count <= 0) {
        return 0;
    }
    for (int32_t i = 0; i < node_count; ++i) {
        in_cycle[i] = 0;
    }
    if (dep_from == nullptr || dep_to == nullptr || edge_count <= 0) {
        return 0;
    }

    // Build adjacency: edge dep_from -> dep_to expresses "dep_to depends on dep_from".
    // We walk dependency edges (node -> its dependency) to find back edges, matching
    // the managed scheduler's recursive visit over task.depends_on.
    std::vector<std::vector<int32_t>> deps(static_cast<size_t>(node_count));
    for (int32_t e = 0; e < edge_count; ++e) {
        const int32_t child = dep_to[e];    // task that has a dependency
        const int32_t parent = dep_from[e]; // the dependency it waits on
        if (child < 0 || child >= node_count || parent < 0 || parent >= node_count) {
            continue;
        }
        deps[static_cast<size_t>(child)].push_back(parent);
    }

    enum State : uint8_t { Unvisited = 0, Visiting = 1, Done = 2 };
    std::vector<uint8_t> state(static_cast<size_t>(node_count), Unvisited);
    std::vector<int32_t> path;
    path.reserve(static_cast<size_t>(node_count));

    // Iterative DFS so deep graphs cannot blow the native stack.
    for (int32_t start = 0; start < node_count; ++start) {
        if (state[static_cast<size_t>(start)] != Unvisited) {
            continue;
        }
        std::vector<std::pair<int32_t, size_t>> stack;
        stack.push_back({start, 0});
        state[static_cast<size_t>(start)] = Visiting;
        path.push_back(start);

        while (!stack.empty()) {
            auto& frame = stack.back();
            const int32_t node = frame.first;
            auto& edges = deps[static_cast<size_t>(node)];
            if (frame.second < edges.size()) {
                const int32_t next = edges[frame.second++];
                if (state[static_cast<size_t>(next)] == Visiting) {
                    // Back edge: everything from `next` to the top of the path is on a cycle.
                    bool mark = false;
                    for (int32_t id : path) {
                        if (id == next) mark = true;
                        if (mark) in_cycle[static_cast<size_t>(id)] = 1;
                    }
                } else if (state[static_cast<size_t>(next)] == Unvisited) {
                    state[static_cast<size_t>(next)] = Visiting;
                    stack.push_back({next, 0});
                    path.push_back(next);
                }
            } else {
                state[static_cast<size_t>(node)] = Done;
                stack.pop_back();
                if (!path.empty()) path.pop_back();
            }
        }
    }

    int32_t flagged = 0;
    for (int32_t i = 0; i < node_count; ++i) {
        if (in_cycle[i]) ++flagged;
    }
    return flagged;
}

} // extern "C"
