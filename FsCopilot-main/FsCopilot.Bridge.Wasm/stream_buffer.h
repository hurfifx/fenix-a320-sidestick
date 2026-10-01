#pragma once
#include <deque>

template <typename T> struct timed_sample
{
    double t_local; // local seconds
    T      value;
};

template <typename T> class stream_buffer
{
  public:
    explicit stream_buffer(const double min_sample_interval_sec = 0.2, const double max_age_sec = 3.0)
        : min_sample_interval_sec_(min_sample_interval_sec),
          max_age_sec_(max_age_sec)
    {
    }

    void push(double t_local, const T& v)
    {
        if (t_local - last_time_ < min_sample_interval_sec_)
        {
            pending_ = {t_local, v};
            has_pending_ = true;
            return;
        }
        last_time_ = t_local;
        prune(t_local);
        buf_.push_back({t_local, v});
        has_pending_ = false;
    }

    template <typename LerpFn> bool interpolate(double t_render, T& out, LerpFn lerp_fn)
    {
        if (buf_.empty())
            return false;

        if (has_pending_ && t_render >= buf_.back().t_local)
        {
            if (pending_.t_local >= buf_.back().t_local)
            {
                prune(pending_.t_local);
                buf_.push_back(pending_);
                last_time_ = pending_.t_local;
            }
            has_pending_ = false;
        }

        const auto n = buf_.size();

        // if only one sample, we can only hold it.
        if (n == 1) { out = buf_[0].value; return true; }

        // if asked time is after the last sample, hold last.
        if (t_render >= buf_[n - 1].t_local)
        {
            out = buf_[n - 1].value;
            return true;
        }

        // if asked time is before the first sample, hold first.
        if (t_render <= buf_[0].t_local) { out = buf_[0].value; return true; }

        // find segment [i, i+1] containing t_render
        uint32_t i = 0;
        while (i + 1 < n && buf_[i + 1].t_local < t_render)
            ++i;

        // i+1 must exist because we handled t_render < last above.
        // if (i + 1 >= n)
        //     return false;

        const auto& a = buf_[i];
        const auto& b = buf_[i + 1];

        // if (t_render < a.t_local || t_render > b.t_local)
        //     return false;

        const double dt    = b.t_local - a.t_local;
        const double alpha = dt <= 1e-9 ? 0.0 : (t_render - a.t_local) / dt;

        lerp_fn(a.value, b.value, alpha, out);
        return true;
    }

  private:
    std::deque<timed_sample<T>> buf_;
    double min_sample_interval_sec_ = 0.2;
    double max_age_sec_ = 3.0;
    double last_time_ = 0;
    timed_sample<T> pending_{};
    bool has_pending_ = false;

    void prune(const double now)
    {
        const double min_t = now - max_age_sec_;
        while (!buf_.empty() && buf_.front().t_local < min_t)
            buf_.pop_front();
    }
};
