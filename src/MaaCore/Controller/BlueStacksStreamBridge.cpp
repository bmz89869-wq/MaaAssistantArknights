#include "BlueStacksStreamBridge.h"

#ifdef _WIN32

#include <atomic>
#include <cstring>
#include <limits>

#include "MaaUtils/NoWarningCV.hpp"
#include "Utils/Logger.hpp"

namespace
{
std::atomic_uint32_t InstanceCounter = 0;
}

asst::BlueStacksStreamBridge::~BlueStacksStreamBridge()
{
    stop();
}

bool asst::BlueStacksStreamBridge::start(
    const std::filesystem::path& bridge_dir,
    const std::filesystem::path& adb_path,
    const std::string& address)
{
    stop();

    const auto executable = bridge_dir / L"BlueStacksStreamBridge.exe";
    const auto server = bridge_dir / L"scrcpy-server-v4.1";
    const auto ffmpeg = adb_path.parent_path() / L"ffmpeg.exe";
    if (!std::filesystem::exists(executable) || !std::filesystem::exists(server) ||
        !std::filesystem::exists(adb_path) || !std::filesystem::exists(ffmpeg)) {
        LogInfo << "BlueStacks stream bridge unavailable" << VAR(executable) << VAR(server) << VAR(adb_path)
                << VAR(ffmpeg);
        return false;
    }

    const auto instance = ++InstanceCounter;
    const auto suffix = std::to_wstring(::GetCurrentProcessId()) + L"." + std::to_wstring(instance);
    m_mapping_name = L"Local\\MAA.BlueStacksStreamBridge." + suffix;
    const auto stop_event_name = L"Local\\MAA.BlueStacksStreamBridge.Stop." + suffix;
    m_stop_event = ::CreateEventW(nullptr, TRUE, FALSE, stop_event_name.c_str());
    if (m_stop_event == nullptr) {
        Log.warn("CreateEventW for BlueStacks stream bridge failed", ::GetLastError());
        return false;
    }

    m_job = ::CreateJobObjectW(nullptr, nullptr);
    if (m_job != nullptr) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits {};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!::SetInformationJobObject(m_job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
            Log.warn("SetInformationJobObject for BlueStacks stream bridge failed", ::GetLastError());
            ::CloseHandle(m_job);
            m_job = nullptr;
        }
    }

    std::wstring command = quote_arg(executable.wstring()) + L" --shared-memory " + quote_arg(m_mapping_name) +
                           L" --stop-event " + quote_arg(stop_event_name) + L" --parent-pid " +
                           std::to_wstring(::GetCurrentProcessId()) + L" --adb " + quote_arg(adb_path.wstring()) +
                           L" --ffmpeg " + quote_arg(ffmpeg.wstring()) + L" --server " + quote_arg(server.wstring()) +
                           L" --serial " + quote_arg(std::wstring(address.begin(), address.end())) +
                           L" --max-size 1280 --max-fps 30 --bit-rate 8000000 --push-server";
    std::vector<wchar_t> mutable_command(command.begin(), command.end());
    mutable_command.push_back(L'\0');

    STARTUPINFOW startup_info {};
    startup_info.cb = sizeof(startup_info);
    PROCESS_INFORMATION process_info {};
    const auto created = ::CreateProcessW(
        executable.c_str(),
        mutable_command.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED,
        nullptr,
        bridge_dir.c_str(),
        &startup_info,
        &process_info);
    if (!created) {
        Log.warn("CreateProcessW for BlueStacks stream bridge failed", ::GetLastError());
        stop();
        return false;
    }

    m_process = process_info.hProcess;
    if (m_job != nullptr && !::AssignProcessToJobObject(m_job, m_process)) {
        Log.warn("AssignProcessToJobObject for BlueStacks stream bridge failed", ::GetLastError());
        ::CloseHandle(m_job);
        m_job = nullptr;
    }
    const auto resume_result = ::ResumeThread(process_info.hThread);
    ::CloseHandle(process_info.hThread);
    if (resume_result == static_cast<DWORD>(-1)) {
        Log.warn("ResumeThread for BlueStacks stream bridge failed", ::GetLastError());
        ::TerminateProcess(m_process, 1);
        ::WaitForSingleObject(m_process, 1000);
        stop();
        return false;
    }
    Log.info(
        "BlueStacks stream bridge started",
        process_info.dwProcessId,
        std::filesystem::path(m_mapping_name));
    m_started_at = std::chrono::steady_clock::now();
    return true;
}

bool asst::BlueStacksStreamBridge::screencap(cv::Mat& image)
{
    constexpr auto StartupWarmup = std::chrono::milliseconds(1500);
    const auto attempt_started_at = std::chrono::steady_clock::now();
    const auto startup_deadline = m_started_at + StartupWarmup;
    const auto input_deadline = attempt_started_at + std::chrono::microseconds(InputFrameWaitUs);

    do {
        const auto required_after_us = m_required_frame_after_us.load(std::memory_order_acquire);
        if (try_screencap(image, required_after_us)) {
            return true;
        }

        const auto deadline = !m_ready_logged ? startup_deadline
                                               : required_after_us > 0 ? input_deadline : attempt_started_at;
        if (m_started_at == std::chrono::steady_clock::time_point {} ||
            std::chrono::steady_clock::now() >= deadline || !process_running()) {
            return false;
        }
        ::Sleep(20);
    } while (true);
}

void asst::BlueStacksStreamBridge::invalidate_after_input() noexcept
{
    m_required_frame_after_us.store(query_performance_counter_us(), std::memory_order_release);
}

bool asst::BlueStacksStreamBridge::try_screencap(cv::Mat& image, std::int64_t required_after_us)
{
    if (!process_running() || (!m_view && !open_mapping())) {
        return false;
    }

    const auto* header = reinterpret_cast<const SharedFrameHeader*>(m_view);
    if (header->magic != Magic || header->version != Version || header->header_bytes != HeaderBytes ||
        header->pixel_format != Bgra || header->width != OutputWidth || header->height != OutputHeight ||
        header->stride != header->width * 4 ||
        header->qpc_frequency <= 0) {
        return false;
    }

    const auto expected_frame_bytes = static_cast<std::int64_t>(header->stride) * header->height;
    if (header->frame_bytes != expected_frame_bytes || header->frame_bytes <= 0 ||
        static_cast<std::size_t>(header->frame_bytes) > MappingBytes - HeaderBytes) {
        return false;
    }

    // InterlockedCompareExchange64 is a read-modify-write operation and faults on FILE_MAP_READ views.
    const std::atomic_ref<LONG64> sequence(const_cast<LONG64&>(header->sequence));

    for (int attempt = 0; attempt != 2; ++attempt) {
        const auto before = sequence.load(std::memory_order_acquire);
        if (before == 0 || (before & 1) != 0) {
            continue;
        }

        const auto width = header->width;
        const auto height = header->height;
        const auto stride = header->stride;
        const auto frame_bytes = header->frame_bytes;
        const auto decoded_host_us = header->decoded_host_us;
        const auto qpc_frequency = header->qpc_frequency;
        m_frame_buffer.resize(static_cast<std::size_t>(frame_bytes));
        std::memcpy(m_frame_buffer.data(), m_view + HeaderBytes, m_frame_buffer.size());

        const auto after = sequence.load(std::memory_order_acquire);
        if (before != after || (after & 1) != 0) {
            continue;
        }

        const auto now_us = query_performance_counter_us(qpc_frequency);
        const auto age_us = now_us - decoded_host_us;
        if (now_us <= 0 || decoded_host_us <= 0 || age_us < -50'000 ||
            (required_after_us > 0 && decoded_host_us < required_after_us)) {
            return false;
        }

        cv::Mat bgra(height, width, CV_8UC4, m_frame_buffer.data(), static_cast<std::size_t>(stride));
        cv::cvtColor(bgra, image, cv::COLOR_BGRA2BGR);
        if (image.empty()) {
            return false;
        }
        if (!m_ready_logged) {
            Log.info("BlueStacks stream bridge first frame accepted", width, height, "age", age_us, "us");
            m_ready_logged = true;
        }

        auto expected_required_after_us = required_after_us;
        if (expected_required_after_us > 0) {
            m_required_frame_after_us.compare_exchange_strong(
                expected_required_after_us,
                0,
                std::memory_order_release,
                std::memory_order_relaxed);
        }
        return true;
    }
    return false;
}

void asst::BlueStacksStreamBridge::stop() noexcept
{
    close_mapping();
    if (m_stop_event != nullptr) {
        ::SetEvent(m_stop_event);
    }
    if (m_process != nullptr && ::WaitForSingleObject(m_process, 8000) == WAIT_TIMEOUT) {
        Log.warn("BlueStacks stream bridge did not stop in time; terminating it");
        ::TerminateProcess(m_process, 1);
        ::WaitForSingleObject(m_process, 1000);
    }
    if (m_process != nullptr) ::CloseHandle(m_process);
    if (m_stop_event != nullptr) ::CloseHandle(m_stop_event);
    if (m_job != nullptr) ::CloseHandle(m_job);
    m_process = nullptr;
    m_stop_event = nullptr;
    m_job = nullptr;
    m_mapping_name.clear();
    m_frame_buffer.clear();
    m_started_at = {};
    m_required_frame_after_us.store(0, std::memory_order_relaxed);
    m_ready_logged = false;
    m_exit_logged = false;
}

bool asst::BlueStacksStreamBridge::open_mapping() noexcept
{
    m_mapping = ::OpenFileMappingW(FILE_MAP_READ, FALSE, m_mapping_name.c_str());
    if (m_mapping == nullptr) {
        return false;
    }
    m_view = static_cast<const std::byte*>(::MapViewOfFile(m_mapping, FILE_MAP_READ, 0, 0, MappingBytes));
    if (m_view != nullptr) {
        return true;
    }
    ::CloseHandle(m_mapping);
    m_mapping = nullptr;
    return false;
}

bool asst::BlueStacksStreamBridge::process_running() noexcept
{
    if (m_process == nullptr) {
        return false;
    }
    const auto wait_result = ::WaitForSingleObject(m_process, 0);
    if (wait_result == WAIT_TIMEOUT) {
        return true;
    }
    if (!m_exit_logged) {
        DWORD exit_code = 0;
        ::GetExitCodeProcess(m_process, &exit_code);
        Log.warn("BlueStacks stream bridge exited", exit_code);
        m_exit_logged = true;
    }
    close_mapping();
    return false;
}

void asst::BlueStacksStreamBridge::close_mapping() noexcept
{
    if (m_view != nullptr) ::UnmapViewOfFile(m_view);
    if (m_mapping != nullptr) ::CloseHandle(m_mapping);
    m_view = nullptr;
    m_mapping = nullptr;
}

std::wstring asst::BlueStacksStreamBridge::quote_arg(const std::wstring& value)
{
    std::wstring result = L"\"";
    std::size_t backslashes = 0;
    for (const auto character : value) {
        if (character == L'\\') {
            ++backslashes;
        }
        else if (character == L'\"') {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(character);
            backslashes = 0;
        }
        else {
            result.append(backslashes, L'\\');
            backslashes = 0;
            result.push_back(character);
        }
    }
    result.append(backslashes * 2, L'\\');
    result.push_back(L'\"');
    return result;
}

std::int64_t asst::BlueStacksStreamBridge::query_performance_counter_us(std::int64_t frequency) noexcept
{
    LARGE_INTEGER counter {};
    if (frequency <= 0 || !::QueryPerformanceCounter(&counter)) {
        return 0;
    }
    const auto seconds = counter.QuadPart / frequency;
    const auto remainder = counter.QuadPart % frequency;
    if (seconds > (std::numeric_limits<std::int64_t>::max)() / 1'000'000) {
        return 0;
    }
    return seconds * 1'000'000 + remainder * 1'000'000 / frequency;
}

std::int64_t asst::BlueStacksStreamBridge::query_performance_counter_us() noexcept
{
    LARGE_INTEGER frequency {};
    return ::QueryPerformanceFrequency(&frequency) ? query_performance_counter_us(frequency.QuadPart) : 0;
}

#endif
