#pragma once

#ifdef _WIN32

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <Windows.h>

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <string>
#include <vector>

#include "MaaUtils/NoWarningCVMat.hpp"

namespace asst
{
class BlueStacksStreamBridge
{
public:
    BlueStacksStreamBridge() = default;
    BlueStacksStreamBridge(const BlueStacksStreamBridge&) = delete;
    BlueStacksStreamBridge(BlueStacksStreamBridge&&) = delete;
    ~BlueStacksStreamBridge();

    BlueStacksStreamBridge& operator=(const BlueStacksStreamBridge&) = delete;
    BlueStacksStreamBridge& operator=(BlueStacksStreamBridge&&) = delete;

    bool start(
        const std::filesystem::path& bridge_dir,
        const std::filesystem::path& adb_path,
        const std::string& address);
    bool screencap(cv::Mat& image);
    void invalidate_after_input() noexcept;
    void stop() noexcept;

private:
#pragma pack(push, 1)
    struct SharedFrameHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t header_bytes;
        std::uint32_t pixel_format;
        std::int32_t width;
        std::int32_t height;
        std::int32_t stride;
        std::int32_t frame_bytes;
        volatile LONG64 sequence;
        volatile LONG64 published_frames;
        std::int64_t decoded_host_us;
        std::int64_t qpc_frequency;
        volatile LONG64 producer_heartbeat_us;
        std::byte reserved[56];
    };
#pragma pack(pop)

    static_assert(sizeof(SharedFrameHeader) == 128);
    static_assert(offsetof(SharedFrameHeader, producer_heartbeat_us) == 64);

    static constexpr std::uint32_t Magic = 0x4241414D; // "MAAB"
    static constexpr std::uint32_t Version = 1;
    static constexpr std::uint32_t Bgra = 0x41524742; // "BGRA"
    static constexpr std::size_t HeaderBytes = 128;
    static constexpr std::size_t MappingBytes = HeaderBytes + 1920ULL * 1080ULL * 4ULL;
    static constexpr std::int32_t OutputWidth = 1280;
    static constexpr std::int32_t OutputHeight = 720;
    static constexpr std::int64_t InputFrameWaitUs = 250'000;
    static constexpr std::int64_t MaximumHeartbeatAgeUs = 1'000'000;
    static constexpr std::int64_t StaticFrameAgeUs = 250'000;

    enum class Status : std::uint8_t
    {
        Stopped,
        Unavailable,
        Starting,
        WaitingForPostInputFrame,
        Ready,
        StaticReuse,
        AdbFallback,
    };

    enum class Reason : std::uint8_t
    {
        None,
        MissingExecutable,
        MissingScrcpyServer,
        MissingAdb,
        MissingFfmpeg,
        StopEventCreationFailed,
        ProcessCreationFailed,
        ProcessResumeFailed,
        MapUnavailable,
        InvalidHeader,
        StaleHeartbeat,
        FrameWriteRace,
        PostInputFramePending,
        PostInputTimeout,
        ProcessExited,
        ConversionFailed,
    };

    bool open_mapping() noexcept;
    bool process_running() noexcept;
    Reason try_screencap(cv::Mat& image, std::int64_t required_after_us);
    void close_mapping() noexcept;
    void stop_locked() noexcept;
    void set_status(Status status, Reason reason = Reason::None) noexcept;
    static const char* status_name(Status status) noexcept;
    static const char* reason_name(Reason reason) noexcept;
    static std::wstring quote_arg(const std::wstring& value);
    static std::int64_t query_performance_counter_us(std::int64_t frequency) noexcept;
    static std::int64_t query_performance_counter_us() noexcept;

    std::mutex m_mutex;
    HANDLE m_process = nullptr;
    HANDLE m_job = nullptr;
    HANDLE m_stop_event = nullptr;
    HANDLE m_mapping = nullptr;
    const std::byte* m_view = nullptr;
    std::wstring m_mapping_name;
    std::vector<std::uint8_t> m_frame_buffer;
    std::chrono::steady_clock::time_point m_started_at;
    std::atomic<std::int64_t> m_required_frame_after_us = 0;
    Status m_status = Status::Stopped;
    Reason m_status_reason = Reason::None;
    bool m_has_accepted_frame = false;
    bool m_exit_logged = false;
};
} // namespace asst

#endif
