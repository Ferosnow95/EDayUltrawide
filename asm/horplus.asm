; EDayUltrawide - Hor+ wrapper for UCineCameraComponent::GetCameraView(this=rcx, float DeltaTime=xmm1, FMinimalViewInfo& View=r8)
; Installed by swapping one vtable slot (data). Calls the game's original, then widens View.FOV so the
; vertical field of view matches what the shot shows at the basis aspect (16:9):
;     FOV' = 2 * atan( tan(FOV/2) * K ),   K = renderAspect / basisAspect   (computed by the tool)
;
; Config block (pointer patched into the two mov r10 immediates):
;   +0x00 dd enabled            +0x08 dq original function     +0x10 dq calls        +0x18 dq adjusted
;   +0x20 dd pi/360             +0x24 dd K                     +0x28 dd 360/pi       +0x2C dd FOV offset in View (0x30)
;   +0x30 dd min FOV (5)        +0x34 dd max FOV (170)         +0x38 dd last FOV in  +0x3C dd last FOV out
bits 64

wrapper:
    push rbx                      ; rsp now 16-aligned
    sub  rsp, 0x20                ; shadow space, stays aligned
    mov  rbx, r8                  ; View (callee-saved across the call)
    mov  r10, 0x1122334455667788  ; CONFIG
    lock inc qword [r10 + 0x10]
    call [r10 + 0x08]             ; original(rcx, xmm1, r8) - arguments untouched

    mov  r10, 0x1122334455667788  ; CONFIG (r10 is volatile)
    cmp  dword [r10], 0
    je   .done
    test rbx, rbx
    jz   .done
    movsxd rax, dword [r10 + 0x2C]
    movss  xmm0, [rbx + rax]
    comiss xmm0, [r10 + 0x30]     ; FOV <= min (or NaN) -> leave
    jbe  .done
    comiss xmm0, [r10 + 0x34]     ; FOV >= max -> leave
    jae  .done
    movss [r10 + 0x38], xmm0

    fld   dword [rbx + rax]
    fmul  dword [r10 + 0x20]      ; half angle in radians
    fptan                         ; st0 = 1.0, st1 = tan
    fstp  st0                     ; st0 = tan
    fmul  dword [r10 + 0x24]      ; * K
    fld1
    fpatan                        ; st0 = atan(tan*K / 1)
    fmul  dword [r10 + 0x28]      ; full angle in degrees
    fstp  dword [rbx + rax]       ; x87 stack empty again

    movss xmm0, [rbx + rax]
    movss [r10 + 0x3C], xmm0
    lock inc qword [r10 + 0x18]
.done:
    add  rsp, 0x20
    pop  rbx
    ret
