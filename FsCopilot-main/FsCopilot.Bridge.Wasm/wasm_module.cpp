#include "wasm_module.h"
#include "var_watcher.h"
#include <SimConnect.h>
#include <chrono>
#include <cmath>
#include <unordered_map>
#include <MSFS/MSFS.h>
#include <MSFS/MSFS_CommBus.h>
#include <MSFS/MSFS_Events.h>
#include <MSFS/MSFS_WindowsTypes.h>
#include <MSFS/Utils/SimParamArrayHelper.h>
#include <MSFS/Legacy/gauges.h>

namespace
{
constexpr auto k_version = "1.2";

enum : DWORD // NOLINT(performance-enum-size)
{
    def_state        = 0xF000,
    def_clock        = 0xF001,
    def_ready        = 0xF101,
    def_control      = 0xF103,
    def_comm_bus_out = 0xF501,
    def_comm_bus_in  = 0xF502,
    def_watch        = 0xF503,
    def_unwatch      = 0xF504,
    def_variable     = 0xF505,
    def_exe          = 0xF506,
    def_physics      = 0xF901,
    def_surfaces     = 0xF902,
    evt_mask_axis_elevator = 0xFA01,
    evt_mask_axis_ailerons = 0xFA02,
    evt_mask_elevator      = 0xFA03,
    evt_mask_aileron       = 0xFA04,
    evt_out_axis_elevator  = 0xFA05,
    evt_out_axis_ailerons  = 0xFA06,
    group_control_mask     = 0xFA10
};

struct freeze_state
{
    int32_t lat_lon_freeze;
    int32_t alt_freeze;
    int32_t att_freeze;
    int32_t sim_disabled;
    int32_t fsc_control;
    int32_t input_only;
};

static_assert(sizeof(freeze_state) == 24);

HANDLE                                h_sim = 0;
std::chrono::steady_clock::time_point g_start;
double                                g_last_seen           = 0;
double                                g_last_ready_sent     = -100.0;
volatile bool                         has_standalone_update = false;
bool                                  frz_by_me             = false;
bool                                  gsx_repositioning     = false;
freeze_state                          frz;
var_watcher                           watcher(1e-6);
fsc::protocol::surfaces               latest_ctrl{};
bool                                  has_latest_ctrl = false;
int32_t                               g_active_control_state = 0;
bool                                  g_replaying_axis_event   = false;
bool                                  g_control_event_mask_enabled = false;
constexpr char                        k_captured_elevator_lvar[] = "FSC_CAPTURED_ELEVATOR_INPUT";
constexpr char                        k_captured_aileron_lvar[]  = "FSC_CAPTURED_AILERON_INPUT";
constexpr double                      k_replayed_axis_ignore_sec  = 0.20;

struct replayed_axis_event_state
{
    int32_t raw_value = 0;
    double  ignore_until = 0.0;
};

replayed_axis_event_state             g_replayed_elevator_event{};
replayed_axis_event_state             g_replayed_aileron_event{};

stream_buffer<fsc::protocol::physics>  phys_buf(k_physics_min_interval_sec, k_max_age_sec);
stream_buffer<fsc::protocol::surfaces> ctrl_buf(k_control_min_interval_sec, k_max_age_sec);

std::unordered_map<uint64_t, time_shift> t_shifts;

bool is_captured_master(const int32_t state)
{
    return state == 3;
}

bool is_captured_slave(const int32_t state)
{
    return state == 4;
}

bool use_a_only_control(const int32_t state)
{
    return is_captured_master(state) || is_captured_slave(state);
}

int32_t effective_control_state()
{
    return g_active_control_state != 0 ? g_active_control_state : frz.fsc_control;
}

bool is_elevator_control_event(const ID32 event_id)
{
    return event_id == KEY_AXIS_ELEVATOR_SET || event_id == KEY_ELEVATOR_SET;
}

bool is_aileron_control_event(const ID32 event_id)
{
    return event_id == KEY_AXIS_AILERONS_SET || event_id == KEY_AILERON_SET;
}

double now_sec()
{
    const auto tp = std::chrono::steady_clock::now();
    return std::chrono::duration<double>(tp - g_start).count();
}

void set_control_event_mask_enabled(const bool enabled)
{
    if (h_sim == 0 || g_control_event_mask_enabled == enabled)
        return;

    (void)SimConnect_ClearNotificationGroup(h_sim, group_control_mask);

    if (enabled)
    {
        (void)SimConnect_AddClientEventToNotificationGroup(h_sim, group_control_mask, evt_mask_axis_elevator, TRUE);
        (void)SimConnect_AddClientEventToNotificationGroup(h_sim, group_control_mask, evt_mask_axis_ailerons, TRUE);
        (void)SimConnect_AddClientEventToNotificationGroup(h_sim, group_control_mask, evt_mask_elevator, TRUE);
        (void)SimConnect_AddClientEventToNotificationGroup(h_sim, group_control_mask, evt_mask_aileron, TRUE);
    }

    (void)SimConnect_SetNotificationGroupPriority(
        h_sim,
        group_control_mask,
        enabled ? SIMCONNECT_GROUP_PRIORITY_HIGHEST_MASKABLE : SIMCONNECT_GROUP_PRIORITY_LOWEST);
    g_control_event_mask_enabled = enabled;
    (void)fprintf(stdout, enabled ? "[FsCopilot] Control event mask enabled" : "[FsCopilot] Control event mask disabled");
}

void remember_replayed_axis_event(const ID32 event_id, const int32_t raw_value)
{
    replayed_axis_event_state* state = nullptr;
    if (is_elevator_control_event(event_id))
        state = &g_replayed_elevator_event;
    else if (is_aileron_control_event(event_id))
        state = &g_replayed_aileron_event;

    if (!state)
        return;

    state->raw_value = raw_value;
    state->ignore_until = now_sec() + k_replayed_axis_ignore_sec;
}

bool is_recent_replayed_axis_event(const ID32 event_id, const int32_t raw_value)
{
    const replayed_axis_event_state* state = nullptr;
    if (is_elevator_control_event(event_id))
        state = &g_replayed_elevator_event;
    else if (is_aileron_control_event(event_id))
        state = &g_replayed_aileron_event;

    if (!state)
        return false;

    return now_sec() <= state->ignore_until && std::abs(state->raw_value - raw_value) <= 1;
}

void set_gsx_repositioning(const bool enabled)
{
    if (gsx_repositioning == enabled)
        return;

    execute_calculator_code(enabled
                                ? "1 (>L:FSDT_GSX_REPOSITIONING) 1 (>L:FSDT_VAR_Frozen)"
                                : "0 (>L:FSDT_GSX_REPOSITIONING) 0 (>L:FSDT_VAR_Frozen)",
                            nullptr, nullptr, nullptr);
    gsx_repositioning = enabled;
}

void set_captured_axis_value(const char* const lvar_name, const int32_t value)
{
    char cmd[128];
    (void)snprintf(cmd, sizeof(cmd), "%d (>L:%s)", value, lvar_name);
    execute_calculator_code(cmd, nullptr, nullptr, nullptr);
}

void transmit_captured_axis_output(const fsc::protocol::surfaces& c)
{
    if (h_sim == 0)
        return;

    (void)SimConnect_TransmitClientEvent_EX1(
        h_sim,
        SIMCONNECT_OBJECT_ID_USER,
        evt_out_axis_ailerons,
        SIMCONNECT_GROUP_PRIORITY_HIGHEST,
        SIMCONNECT_EVENT_FLAG_GROUPID_IS_PRIORITY,
        static_cast<DWORD>(-c.ail_pos));
    (void)SimConnect_TransmitClientEvent_EX1(
        h_sim,
        SIMCONNECT_OBJECT_ID_USER,
        evt_out_axis_elevator,
        SIMCONNECT_GROUP_PRIORITY_HIGHEST,
        SIMCONNECT_EVENT_FLAG_GROUPID_IS_PRIORITY,
        static_cast<DWORD>(-c.elev_pos));
}

void send_ready(const double now, const bool force = false)
{
    if (h_sim == 0)
        return;

    if (!force && now - g_last_ready_sent < 1.0)
        return;

    fsc::protocol::str_msg msg{};
    (void)snprintf(msg.msg, sizeof(msg.msg), "%s", k_version);
    (void)SimConnect_SetClientData(h_sim, def_ready, def_ready, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, sizeof(msg), &msg);
    g_last_ready_sent = now;
    (void)fprintf(stdout, "[FsCopilot] Ready version %s sent", msg.msg);
}

void neutralize_axis_event(const ID32 event_id)
{
    g_replaying_axis_event = true;
    const auto recent_replay = [&](const replayed_axis_event_state& state) -> bool { return now_sec() <= state.ignore_until; };
    const auto latest_axis_value = [&](const replayed_axis_event_state& replay_state, const bool elevator_axis) -> int32_t
    {
        if (has_latest_ctrl)
        {
            if (elevator_axis)
                return -latest_ctrl.elev_pos;

            return -latest_ctrl.ail_pos;
        }

        return recent_replay(replay_state) ? replay_state.raw_value : 0;
    };

    if (is_elevator_control_event(event_id))
    {
        const auto raw_value = latest_axis_value(g_replayed_elevator_event, true);
        if (event_id == KEY_ELEVATOR_SET)
        {
            send_key_event(KEY_ELEVATOR_SET, static_cast<UINT32>(raw_value));
        }
        else
        {
            auto params = FsCreateParamArray("i", static_cast<unsigned int>(raw_value));
            fsEventsTriggerKeyEvent(KEY_AXIS_ELEVATOR_SET, params);
            FsDestroyParamArray(&params);
        }
    }
    else if (is_aileron_control_event(event_id))
    {
        const auto raw_value = latest_axis_value(g_replayed_aileron_event, false);
        if (event_id == KEY_AILERON_SET)
        {
            send_key_event(KEY_AILERON_SET, static_cast<UINT32>(raw_value));
        }
        else
        {
            auto params = FsCreateParamArray("i", static_cast<unsigned int>(raw_value));
            fsEventsTriggerKeyEvent(KEY_AXIS_AILERONS_SET, params);
            FsDestroyParamArray(&params);
        }
    }
    g_replaying_axis_event = false;
}

void handle_key_event(const ID32 event, const int32_t raw_value)
{
    if (g_replaying_axis_event)
        return;

    if (is_recent_replayed_axis_event(event, raw_value))
        return;

    if (is_elevator_control_event(event))
    {
        set_captured_axis_value(k_captured_elevator_lvar, raw_value);
        if (use_a_only_control(effective_control_state()))
            neutralize_axis_event(event);
    }
    else if (is_aileron_control_event(event))
    {
        set_captured_axis_value(k_captured_aileron_lvar, raw_value);
        if (use_a_only_control(effective_control_state()))
            neutralize_axis_event(event);
    }
}

void on_key_event(const ID32 event, const UINT32 evdata, PVOID /*userdata*/)
{
    handle_key_event(event, static_cast<int32_t>(evdata));
}

void on_key_event_ex1(const ID32 event, const UINT32 evdata0, const UINT32 /*evdata1*/, const UINT32 /*evdata2*/,
                      const UINT32 /*evdata3*/, const UINT32 /*evdata4*/, PVOID /*userdata*/)
{
    handle_key_event(event, static_cast<int32_t>(evdata0));
}

void apply_physics(const fsc::protocol::physics& p)
{
    char cmd[1792];
    if (frz.input_only)
    {
        (void)snprintf(cmd, sizeof(cmd),
                       // Position
                       "%.12f (>A:PLANE LATITUDE, Radians) "
                       "%.12f (>A:PLANE LONGITUDE, Radians) "
                       "%.3f  (>A:PLANE ALTITUDE, Feet) "
                       // Attitude / Heading (Degrees)
                       "%.6f  (>A:PLANE PITCH DEGREES, Degrees) "
                       "%.6f  (>A:PLANE BANK DEGREES, Degrees) "
                       "%.6f  (>A:PLANE HEADING DEGREES GYRO, Degrees) "
                       "%.6f  (>A:PLANE HEADING DEGREES TRUE, Degrees) "
                       // Rates / Forces
                       "%.6f  (>A:VERTICAL SPEED, Feet per second) "
                       "%.6f  (>A:G FORCE, Gforce) "
                       // Velocities (body/world)
                       "%.6f  (>A:VELOCITY BODY Y, Feet per second) "
                       "%.6f  (>A:VELOCITY BODY Z, Feet per second) "
                       "%.6f  (>A:VELOCITY WORLD X, Feet per second) "
                       "%.6f  (>A:VELOCITY WORLD Z, Feet per second) "
                       "%.6f  (>A:VELOCITY WORLD Y, Feet per second) "
                       // ToLiss A340 companion vars keep the custom FM coherent during external repositioning.
                       "(A:PLANE HEADING DEGREES MAGNETIC, Degrees) (>L:TLS_FM_MAGNETIC_HEADING) "
                       "(A:GPS GROUND TRUE TRACK, Degrees) (>L:TLS_FM_TRACK_DEGT) "
                       "(A:GROUND VELOCITY, Meters per second) (>L:TLS_FM_GROUND_SPEED_MS) "
                       "(A:TURN INDICATOR RATE, Radians per second) (>L:TLS_FM_YAW_RATE) "
                       "(A:G FORCE, Gforce) (>L:TLS_FM_GLOAD) "
                       "(A:G FORCE, Gforce) (>L:TLS_FM_GLOAD_COCKPIT) "
                       "1 (>L:TLS_VALID_POS_AFTER_POSCHANGE) "
                       "(A:SIM ON GROUND, Bool) (>L:TLS_GROUND_SENSOR_AFTER_POSCHANGE)",
                       p.lat, p.lon, p.alt_feet, p.pitch, p.bank, p.hdg_deg_gyro, p.hdg_deg_true, p.vertical_speed, p.g_force, p.v_body_y, p.v_body_z, p.vx, p.vz, p.vy);
        set_gsx_repositioning(true);
    }
    else
    {
        (void)snprintf(cmd, sizeof(cmd),
                       // Position
                       "%.12f (>A:PLANE LATITUDE, Radians) "
                       "%.12f (>A:PLANE LONGITUDE, Radians) "
                       "%.3f  (>A:PLANE ALTITUDE, Feet) "
                       // Attitude / Heading (Degrees)
                       "%.6f  (>A:PLANE PITCH DEGREES, Degrees) "
                       "%.6f  (>A:PLANE BANK DEGREES, Degrees) "
                       "%.6f  (>A:PLANE HEADING DEGREES GYRO, Degrees) "
                       "%.6f  (>A:PLANE HEADING DEGREES TRUE, Degrees) "
                       // Rates / Forces
                       "%.6f  (>A:VERTICAL SPEED, Feet per second) "
                       "%.6f  (>A:G FORCE, Gforce) "
                       // Velocities (body/world)
                       "%.6f  (>A:VELOCITY BODY Y, Feet per second) "
                       "%.6f  (>A:VELOCITY BODY Z, Feet per second) "
                       "%.6f  (>A:VELOCITY WORLD X, Feet per second) "
                       "%.6f  (>A:VELOCITY WORLD Z, Feet per second) "
                       "%.6f  (>A:VELOCITY WORLD Y, Feet per second)",
                       p.lat, p.lon, p.alt_feet, p.pitch, p.bank, p.hdg_deg_gyro, p.hdg_deg_true, p.vertical_speed, p.g_force, p.v_body_y, p.v_body_z, p.vx, p.vz, p.vy);
    }
    execute_calculator_code(cmd, nullptr, nullptr, nullptr);
}

void apply_control(const fsc::protocol::surfaces& c)
{
    char cmd[256];
    const auto control_state = effective_control_state();
    const bool captured_master = is_captured_master(control_state);

    if (!captured_master)
    {
        (void)snprintf(cmd, sizeof(cmd),
                       "%d (>A:AILERON POSITION, Position 16k) "
                       "%d (>A:ELEVATOR POSITION, Position 16k) "
                       "%d (>A:RUDDER POSITION, Position 16k)",
                       c.ail_pos, c.elev_pos, c.rud_pos);
        execute_calculator_code(cmd, nullptr, nullptr, nullptr);
    }

    g_replaying_axis_event = true;
    if (captured_master)
    {
        remember_replayed_axis_event(KEY_AXIS_AILERONS_SET, -c.ail_pos);
        remember_replayed_axis_event(KEY_AXIS_ELEVATOR_SET, -c.elev_pos);
        (void)snprintf(cmd, sizeof(cmd),
                       "%d (>K:AXIS_AILERONS_SET) "
                       "%d (>K:AXIS_ELEVATOR_SET)",
                       -c.ail_pos, -c.elev_pos);
    }
    else
    {
        remember_replayed_axis_event(KEY_AXIS_AILERONS_SET, -c.ail_pos);
        remember_replayed_axis_event(KEY_AXIS_ELEVATOR_SET, -c.elev_pos);
        (void)snprintf(cmd, sizeof(cmd),
                       "%d (>K:AXIS_AILERONS_SET) "
                       "%d (>K:AXIS_ELEVATOR_SET) "
                       "%d (>K:AXIS_RUDDER_SET)",
                       -c.ail_pos, -c.elev_pos, -c.rud_pos);
    }
    execute_calculator_code(cmd, nullptr, nullptr, nullptr);
    g_replaying_axis_event = false;
}

void apply_control(const fsc::protocol::control control)
{
    if (frz.sim_disabled)
    {
        g_active_control_state = 0;
        set_control_event_mask_enabled(false);
        return;
    }

    g_active_control_state = control.state;
    g_last_seen = now_sec();
    set_control_event_mask_enabled(false);

    if (frz.fsc_control != control.state)
    {
        char cmd[256];
        (void)snprintf(cmd, sizeof(cmd), "%d (>L:FSC_CONTROL)", control.state);
        execute_calculator_code(cmd, nullptr, nullptr, nullptr);
    }

    const bool sim_frozen = frz.alt_freeze || frz.att_freeze || frz.lat_lon_freeze;
    const bool wants_freeze = control.state == 2 || is_captured_slave(control.state);

    if (wants_freeze && !sim_frozen)
    {
        execute_calculator_code("1 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                "1 (>K:FREEZE_ALTITUDE_SET) "
                                "1 (>K:FREEZE_ATTITUDE_SET)",
                                nullptr, nullptr, nullptr);
        frz_by_me = true;
        if (frz.input_only)
            set_gsx_repositioning(true);
        else
            set_gsx_repositioning(false);
        (void)fprintf(stdout, frz.input_only ? "Freeze engaged with GSX reposition shim" : "Freeze engaged");
    }
    else if (wants_freeze && sim_frozen)
    {
        frz_by_me = true;
        if (frz.input_only)
            set_gsx_repositioning(true);
        else
            set_gsx_repositioning(false);
    }
    else if (!wants_freeze && sim_frozen)
    {
        execute_calculator_code("0 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                "0 (>K:FREEZE_ALTITUDE_SET) "
                                "0 (>K:FREEZE_ATTITUDE_SET)",
                                nullptr, nullptr, nullptr);
        frz_by_me = false;
        set_gsx_repositioning(false);
        (void)fprintf(stdout, "Freeze released");
    }
    else if (!wants_freeze)
    {
        frz_by_me = false;
        set_gsx_repositioning(false);
    }
}

void interpolate(const double now)
{
    if (is_captured_master(effective_control_state()))
    {
        // The flying/master side should feel as close to native as possible.
        // Bypass time-shift/buffer interpolation entirely here and replay the
        // most recent local surfaces sample immediately.
        if (has_latest_ctrl)
            apply_control(latest_ctrl);
        return;
    }

    if (!frz_by_me)
        return;

    const double physics_render_t = now - k_physics_delay_sec;
    const double control_render_t = now - k_control_delay_sec;

    // Physics
    fsc::protocol::physics p{};
    if (phys_buf.interpolate(physics_render_t, p, fsc::interp::physics))
        apply_physics(p);

    // Control
    fsc::protocol::surfaces c{};
    if (ctrl_buf.interpolate(control_render_t, c, fsc::interp::control))
        apply_control(c);
}

void receive_gauge_msg(const char* json, unsigned int size, void* /*ctx*/)
{
    // const size_t len = std::strlen(json);
    if (size > sizeof(fsc::protocol::str_msg::msg))
        return;

    fsc::protocol::str_msg msg{};
    std::memcpy(msg.msg, json, size);

    (void)SimConnect_SetClientData(h_sim, def_comm_bus_out, def_comm_bus_out, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, sizeof(fsc::protocol::str_msg), &msg);
    (void)fprintf(stdout, "Send %s", json);
}

void var_update(const char* name, const double value, void* /*user*/)
{
    (void)fprintf(stdout, "%s -> %f", name, value);
    fsc::protocol::var_set msg = {};
    strncpy(msg.name, name, sizeof(msg.name) - 1);
    msg.value = value;
    (void)SimConnect_SetClientData(h_sim, def_variable, def_variable, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, sizeof(fsc::protocol::var_set), &msg);
}

void CALLBACK dispatch(SIMCONNECT_RECV* p_data, DWORD /*cb_data*/, void* /*p_context*/)
{
    if (!p_data)
        return;

    const auto tp  = std::chrono::steady_clock::now();
    const auto now = std::chrono::duration<double>(tp - g_start).count();

    if (p_data->dwID == SIMCONNECT_RECV_ID_SIMOBJECT_DATA)
    {
        const auto* d = reinterpret_cast<SIMCONNECT_RECV_SIMOBJECT_DATA*>(p_data);
        if (d->dwRequestID == def_state)
        {
            const auto* freeze = reinterpret_cast<const freeze_state*>(&d->dwData);
            frz                = *freeze;
        }
    }

    if (p_data->dwID == SIMCONNECT_RECV_ID_CLIENT_DATA)
    {
        const auto* cd = reinterpret_cast<SIMCONNECT_RECV_CLIENT_DATA*>(p_data);

        // If MSFS2024 Update_StandAlone is active, we don't want double-ticking.
        // BUT we still keep SimConnect alive and continue handling messages above.
        // MSFS2020 fallback tick: we use our clock packets as "per-frame" pulse
        if (!has_standalone_update && cd->dwRequestID == def_clock)
        {
            send_ready(now);
            interpolate(now);
            watcher.poll(&var_update, nullptr);
            if (effective_control_state() != 0 && now - g_last_seen > 1)
            {
                execute_calculator_code("0 (>L:FSC_CONTROL) "
                                        "0 (>L:FSC_INPUT_ONLY) "
                                        "0 (>L:FSDT_GSX_REPOSITIONING) "
                                        "0 (>L:FSDT_VAR_Frozen) "
                                        "0 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                        "0 (>K:FREEZE_ALTITUDE_SET) "
                                        "0 (>K:FREEZE_ATTITUDE_SET)",
                                        nullptr, nullptr, nullptr);
                g_active_control_state = 0;
                set_control_event_mask_enabled(false);
                gsx_repositioning = false;
                frz_by_me = false;
                watcher.clear();
            }
        }

        if (cd->dwRequestID == def_control)
        {
            const auto* ctrl = reinterpret_cast<const fsc::protocol::control*>(&cd->dwData);
            apply_control(*ctrl);
        }

        if (cd->dwRequestID == def_physics)
        {
            const auto*  physics = reinterpret_cast<const fsc::protocol::physics*>(&cd->dwData);
            const double t_local = t_shifts[physics->session_id].to_local_sec(now, physics->time_ms);
            phys_buf.push(t_local, *physics);
        }

        if (cd->dwRequestID == def_surfaces)
        {
            const auto* ctrl = reinterpret_cast<const fsc::protocol::surfaces*>(&cd->dwData);
            latest_ctrl = *ctrl;
            has_latest_ctrl = true;
            const double t_local = t_shifts[ctrl->session_id].to_local_sec(now, ctrl->time_ms);
            ctrl_buf.push(t_local, *ctrl);
        }

        if (cd->dwRequestID == def_comm_bus_in)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::str_msg*>(&cd->dwData);
            (void)fprintf(stdout, "Receive %s", msg->msg);
            fsCommBusCall("FSC_CLIENT_EVENT", msg->msg, sizeof(msg->msg), FsCommBusBroadcast_JS);
        }

        if (cd->dwRequestID == def_watch)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::var_set*>(&cd->dwData);
            if (watcher.watch(msg->name, msg->units))
                (void)fprintf(stdout, "Watch %s, %s", msg->name, msg->units);
        }

        if (cd->dwRequestID == def_unwatch)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::var_set*>(&cd->dwData);
            watcher.unwatch(msg->name);
            (void)fprintf(stdout, "Unwatch %s", msg->name);
        }

        if (cd->dwRequestID == def_exe)
        {
            const auto* msg = reinterpret_cast<const fsc::protocol::str_msg*>(&cd->dwData);
            execute_calculator_code(msg->msg, nullptr, nullptr, nullptr);
            (void)fprintf(stdout, "%s", msg->msg);
        }
    }
}
} // namespace

extern "C" MSFS_CALLBACK void module_init(void)
{
    g_start = std::chrono::steady_clock::now();
    (void)fprintf(stdout, "[FsCopilot] module_init start");

    if (FAILED(SimConnect_Open(&h_sim, "FsCopilot Bridge", nullptr, 0, 0, 0)))
    {
        (void)fprintf(stdout, "[FsCopilot] SimConnect_Open failed");
        return;
    }

    (void)fprintf(stdout, "[FsCopilot] SimConnect_Open ok");

    // state
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "IS LATITUDE LONGITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "IS ALTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "IS ATTITUDE FREEZE ON", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "SIM DISABLED", "Bool", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "L:FSC_CONTROL", "Number", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_AddToDataDefinition(h_sim, def_state, "L:FSC_INPUT_ONLY", "Number", SIMCONNECT_DATATYPE_INT32);
    (void)SimConnect_RequestDataOnSimObject(h_sim, def_state, def_state, SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD_SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG_CHANGED, 0, 0, 0);

    // clock. This is required to drive interpolation in MSFS 2020, where Update_StandAlone is not available.
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_CLOCK", def_clock);
    (void)SimConnect_CreateClientData(h_sim, def_clock, 4, 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_clock, 0, 4, 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_clock, def_clock, def_clock, SIMCONNECT_CLIENT_DATA_PERIOD_VISUAL_FRAME, 0, 0, 0, 0);

    // ready
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_READY", def_ready);
    (void)SimConnect_CreateClientData(h_sim, def_ready, sizeof(fsc::protocol::str_msg), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_ready, 0, sizeof(fsc::protocol::str_msg), 0, 0);
    send_ready(0.0, true);

    // control
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_CONTROL", def_control);
    (void)SimConnect_CreateClientData(h_sim, def_control, sizeof(fsc::protocol::control), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_control, 0, sizeof(fsc::protocol::control), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_control, def_control, def_control, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // physics
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_PHYSICS", def_physics);
    (void)SimConnect_CreateClientData(h_sim, def_physics, sizeof(fsc::protocol::physics), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_physics, 0, sizeof(fsc::protocol::physics), 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_physics, def_physics, def_physics, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // surfaces
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_SURFACES", def_surfaces);
    (void)SimConnect_CreateClientData(h_sim, def_surfaces, sizeof(fsc::protocol::surfaces), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_surfaces, 0, sizeof(fsc::protocol::surfaces), 0.0f, -1);
    (void)SimConnect_RequestClientData(h_sim, def_surfaces, def_surfaces, def_surfaces, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // In captured A320 mode the selected FS Copilot joystick must be the only pitch/roll path.
    // Masking these sim events prevents the native MSFS controller binding from reaching Fenix.
    (void)SimConnect_MapClientEventToSimEvent(h_sim, evt_mask_axis_elevator, "AXIS_ELEVATOR_SET");
    (void)SimConnect_MapClientEventToSimEvent(h_sim, evt_mask_axis_ailerons, "AXIS_AILERONS_SET");
    (void)SimConnect_MapClientEventToSimEvent(h_sim, evt_mask_elevator, "ELEVATOR_SET");
    (void)SimConnect_MapClientEventToSimEvent(h_sim, evt_mask_aileron, "AILERON_SET");
    (void)SimConnect_MapClientEventToSimEvent(h_sim, evt_out_axis_elevator, "AXIS_ELEVATOR_SET");
    (void)SimConnect_MapClientEventToSimEvent(h_sim, evt_out_axis_ailerons, "AXIS_AILERONS_SET");
    (void)SimConnect_SetNotificationGroupPriority(h_sim, group_control_mask, SIMCONNECT_GROUP_PRIORITY_LOWEST);

    // bus
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_BUS_OUT", def_comm_bus_out);
    (void)SimConnect_CreateClientData(h_sim, def_comm_bus_out, sizeof(fsc::protocol::str_msg), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_comm_bus_out, 0, sizeof(fsc::protocol::str_msg), 0, 0);
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_BUS_IN", def_comm_bus_in);
    (void)SimConnect_CreateClientData(h_sim, def_comm_bus_in, sizeof(fsc::protocol::str_msg), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_comm_bus_in, 0, sizeof(fsc::protocol::str_msg), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_comm_bus_in, def_comm_bus_in, def_comm_bus_in, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);
    fsCommBusRegister("FSC_GAUGE_EVENT", receive_gauge_msg, nullptr);

    // watch
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_WATCH", def_watch);
    (void)SimConnect_CreateClientData(h_sim, def_watch, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_watch, 0, sizeof(fsc::protocol::var_set), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_watch, def_watch, def_watch, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // unwatch
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_UNWATCH", def_unwatch);
    (void)SimConnect_CreateClientData(h_sim, def_unwatch, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_unwatch, 0, sizeof(fsc::protocol::var_set), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_unwatch, def_unwatch, def_unwatch, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    // watch demon
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_VARIABLE", def_variable);
    (void)SimConnect_CreateClientData(h_sim, def_variable, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_variable, 0, sizeof(fsc::protocol::var_set), 0, 0);

    // set
    (void)SimConnect_MapClientDataNameToID(h_sim, "FSC_EXE", def_exe);
    (void)SimConnect_CreateClientData(h_sim, def_exe, sizeof(fsc::protocol::var_set), 0);
    (void)SimConnect_AddToClientDataDefinition(h_sim, def_exe, 0, sizeof(fsc::protocol::var_set), 0, 0);
    (void)SimConnect_RequestClientData(h_sim, def_exe, def_exe, def_exe, SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, 0, 0, 0, 0);

    (void)SimConnect_CallDispatch(h_sim, dispatch, nullptr);

    set_captured_axis_value(k_captured_elevator_lvar, 0);
    set_captured_axis_value(k_captured_aileron_lvar, 0);
    register_key_event_handler(on_key_event, nullptr);
    register_key_event_handler_EX1(on_key_event_ex1, nullptr);
    send_ready(0.0, true);

    (void)fprintf(stdout, "Initialized");
}

extern "C" MSFS_CALLBACK void module_deinit(void)
{
    unregister_key_event_handler(on_key_event, nullptr);
    unregister_key_event_handler_EX1(on_key_event_ex1, nullptr);
    set_control_event_mask_enabled(false);
    set_captured_axis_value(k_captured_elevator_lvar, 0);
    set_captured_axis_value(k_captured_aileron_lvar, 0);
    execute_calculator_code("0 (>L:FSC_INPUT_ONLY) 0 (>L:FSDT_GSX_REPOSITIONING) 0 (>L:FSDT_VAR_Frozen)",
                            nullptr, nullptr, nullptr);
    g_active_control_state = 0;

    if (h_sim != 0)
        (void)SimConnect_ClearNotificationGroup(h_sim, group_control_mask);

    if (h_sim != 0)
        (void)SimConnect_Close(h_sim);
    fsCommBusUnregisterOneEvent("FSC_GAUGE_EVENT", receive_gauge_msg, nullptr);
    h_sim = 0;
    (void)fprintf(stdout, "Deinitialized");
}

// MSFS2024 standalone update hook
// ReSharper disable once CppInconsistentNaming
extern "C" MSFS_CALLBACK void Update_StandAlone(float d_time)
{
    const auto tp  = std::chrono::steady_clock::now();
    const auto now = std::chrono::duration<double>(tp - g_start).count();
    // now += static_cast<double>(d_time);
    has_standalone_update = true;
    send_ready(now);
    interpolate(now);
    watcher.poll(&var_update, nullptr);
    if (effective_control_state() != 0 && now - g_last_seen > 1)
    {
        execute_calculator_code("0 (>L:FSC_CONTROL) "
                                "0 (>L:FSC_INPUT_ONLY) "
                                "0 (>L:FSDT_GSX_REPOSITIONING) "
                                "0 (>L:FSDT_VAR_Frozen) "
                                "0 (>K:FREEZE_LATITUDE_LONGITUDE_SET) "
                                "0 (>K:FREEZE_ALTITUDE_SET) "
                                "0 (>K:FREEZE_ATTITUDE_SET)",
                                nullptr, nullptr, nullptr);
        g_active_control_state = 0;
        set_control_event_mask_enabled(false);
        gsx_repositioning = false;
        frz_by_me = false;
        watcher.clear();
    }
}
