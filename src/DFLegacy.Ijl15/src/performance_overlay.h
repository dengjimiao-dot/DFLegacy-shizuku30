#pragma once

#include <cstdint>
#include <cstring>

namespace dfl::performance_overlay
{
    namespace detail
    {
        using BeginTextFunction = void(__thiscall*)(void*, const void*);
        using EndTextFunction = void(__thiscall*)(void*);

        // DF2008's BeginText borrows the descriptor and ignores nested begins.
        // Suspend an outer block, then release our stack descriptor before return.
        class ScopedTextDrawing
        {
        public:
            ScopedTextDrawing(
                void* renderer,
                BeginTextFunction beginText,
                EndTextFunction endText) noexcept
                : renderer_(renderer), beginText_(beginText), endText_(endText)
            {
                const auto* bytes = static_cast<const std::uint8_t*>(renderer_);
                previousActive_ = bytes[0x2D] != 0;
                std::memcpy(&previousState_, bytes + 0x40, sizeof(previousState_));
                endText_(renderer_);
            }

            ~ScopedTextDrawing() noexcept
            {
                endText_(renderer_);
                if (previousActive_)
                {
                    beginText_(renderer_, previousState_);
                }
            }

            ScopedTextDrawing(const ScopedTextDrawing&) = delete;
            ScopedTextDrawing& operator=(const ScopedTextDrawing&) = delete;

        private:
            void* renderer_;
            BeginTextFunction beginText_;
            EndTextFunction endText_;
            const void* previousState_ = nullptr;
            bool previousActive_ = false;
        };
    }

    // Redirect the 2008DF performance-draw call to the dual native/IJL15 hook.
    bool InstallCallHook() noexcept;
}
