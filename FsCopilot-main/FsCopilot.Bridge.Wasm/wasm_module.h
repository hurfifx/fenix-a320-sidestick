#pragma once

#include <cstdio>

#include "protocol.h"
#include "interpolators.h"
#include "stream_buffer.h"
#include "time_shift.h"

constexpr double k_physics_delay_sec = 0.12;
constexpr double k_captured_master_control_delay_sec = 0.0;
constexpr double k_control_delay_sec = 0.015;
constexpr double k_physics_min_interval_sec = 0.05;
constexpr double k_control_min_interval_sec = 0.005;
constexpr double k_max_age_sec = 3.0;
constexpr double k_offset_smoothing = 0.01;
