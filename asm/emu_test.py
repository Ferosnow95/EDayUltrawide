import struct, math, sys
from unicorn import *
from unicorn.x86_const import *
code=open('horplus.bin','rb').read()
CODE,ORIG,CFG,VIEW,STACK,HALT=0x10000,0x11000,0x20000,0x30000,0x80000,0x12000
MARK=(0x1122334455667788).to_bytes(8,'little')
fails=0
def ok(c,n):
    global fails
    print(("PASS " if c else "FAIL ")+n); fails+= (not c)
# fake original: store rcx, xmm1, r8, rsp&15 into VIEW+0x100.. ; write FOV (from VIEW+0x200) to [r8+0x30]
# mov [VIEW+0x100], rcx ; movss [VIEW+0x108], xmm1 ; mov [VIEW+0x110], r8 ; mov rax,rsp ; mov [VIEW+0x118], rax ; mov eax,[VIEW+0x200]; mov [r8+0x30],eax ; ret
import subprocess, tempfile, os
src=f"""bits 64
mov r11, {VIEW+0x100}
mov [r11], rcx
movss [r11+8], xmm1
mov [r11+0x10], r8
mov rax, rsp
mov [r11+0x18], rax
mov eax, [r11+0x100]
mov [r8+0x30], eax
fldz          ; leave nothing on x87 stack: push then pop
fstp st0
ret
"""
d=tempfile.mkdtemp(); open(d+'/o.asm','w').write(src)
subprocess.check_call(['nasm','-f','bin',d+'/o.asm','-o',d+'/o.bin'])
orig=open(d+'/o.bin','rb').read()
def run(fov_in, enabled=1, K=2.0, fovoff=0x30, rcx=0xDEADBEEF, dt=0.016, r8=VIEW):
    mu=Uc(UC_ARCH_X86,UC_MODE_64)
    for a in (CODE,CFG,VIEW,STACK-0x10000): mu.mem_map(a,0x10000)
    c=code.replace(MARK,struct.pack('<Q',CFG))
    mu.mem_write(CODE,c); mu.mem_write(ORIG,orig); mu.mem_write(HALT,b'\xf4')
    cfg=bytearray(0x40)
    struct.pack_into('<i',cfg,0,enabled); struct.pack_into('<Q',cfg,8,ORIG)
    struct.pack_into('<ffffiff',cfg,0x20,math.pi/360,K,360/math.pi,fovoff,5.0,170.0) if False else None
    struct.pack_into('<f',cfg,0x20,math.pi/360); struct.pack_into('<f',cfg,0x24,K); struct.pack_into('<f',cfg,0x28,360/math.pi)
    struct.pack_into('<i',cfg,0x2C,fovoff); struct.pack_into('<f',cfg,0x30,5.0); struct.pack_into('<f',cfg,0x34,170.0)
    mu.mem_write(CFG,bytes(cfg))
    mu.mem_write(VIEW+0x200, struct.pack('<f',fov_in))
    rsp=STACK-0x1000-8   # as if just called: rsp = 16n+8
    mu.mem_write(rsp, struct.pack('<Q',HALT))
    mu.reg_write(UC_X86_REG_RSP,rsp); mu.reg_write(UC_X86_REG_RCX,rcx); mu.reg_write(UC_X86_REG_R8,r8)
    mu.reg_write(UC_X86_REG_RBX,0x1234567)
    mu.reg_write(UC_X86_REG_XMM1, struct.unpack('<I',struct.pack('<f',dt))[0])
    mu.emu_start(CODE, HALT, count=10000)
    g=lambda a,f: struct.unpack(f,mu.mem_read(a,struct.calcsize(f)))[0]
    return dict(out=g(VIEW+0x30,'<f'), calls=g(CFG+0x10,'<Q'), adj=g(CFG+0x18,'<Q'), lin=g(CFG+0x38,'<f'), lout=g(CFG+0x3C,'<f'),
                rcx=g(VIEW+0x100,'<Q'), dt=g(VIEW+0x108,'<f'), r8=g(VIEW+0x110,'<Q'), rsp_at_orig=g(VIEW+0x118,'<Q'),
                rbx=mu.reg_read(UC_X86_REG_RBX), rsp_end=mu.reg_read(UC_X86_REG_RSP), rsp0=rsp, fpsw=mu.reg_read(UC_X86_REG_FPSW))
K=(5120/1440)/(16/9)
r=run(40.0,K=K)
exp=2*math.degrees(math.atan(math.tan(math.radians(20))*K))
ok(abs(r['out']-exp)<1e-3, "FOV 40 -> %.4f (expected %.4f, K=%.4f)"%(r['out'],exp,K))
ok(r['calls']==1 and r['adj']==1 and abs(r['lin']-40)<1e-6 and abs(r['lout']-r['out'])<1e-6, "counters + last in/out")
ok(r['rcx']==0xDEADBEEF and abs(r['dt']-0.016)<1e-7 and r['r8']==VIEW, "original got rcx/xmm1/r8 unchanged")
ok((r['rsp_at_orig']+8)%16==0, "stack 16-aligned at the call into original (rsp after its return-address push = %#x)"%r['rsp_at_orig'])
ok(r['rbx']==0x1234567, "rbx restored")
ok(r['rsp_end']==r['rsp0']+8, "stack balanced on return")
ok(((r['fpsw']>>11)&7)==0, "x87 stack top back to 0 (TOP=%d)"%((r['fpsw']>>11)&7))
# vertical FOV check: vertical at 32:9 after fix == vertical at 16:9 before
v16=2*math.atan(math.tan(math.radians(20))/(16/9)); v32=2*math.atan(math.tan(math.radians(r['out']/2))/(5120/1440))
ok(abs(v16-v32)<1e-5, "vertical FOV at 32:9 (%.4f deg) == vertical at 16:9 (%.4f deg)"%(math.degrees(v32),math.degrees(v16)))
r=run(40.0,enabled=0,K=K); ok(r['out']==40.0 and r['calls']==1 and r['adj']==0, "disabled: passthrough")
r=run(3.0,K=K); ok(r['out']==3.0 and r['adj']==0, "FOV below min untouched")
r=run(175.0,K=K); ok(r['out']==175.0 and r['adj']==0, "FOV above max untouched")
r=run(float('nan'),K=K); ok(math.isnan(r['out']) and r['adj']==0, "NaN untouched")
r=run(90.0,K=1.0); ok(abs(r['out']-90.0)<1e-3, "K=1 identity (%.5f)"%r['out'])
for f in (10,25,60,100,150):
    r=run(float(f),K=K); e=2*math.degrees(math.atan(math.tan(math.radians(f/2))*K))
    ok(abs(r['out']-e)<2e-3 and r['out']<180, "FOV %d -> %.3f (exp %.3f)"%(f,r['out'],e))
print("ALL PASSED" if fails==0 else "%d FAILED"%fails); sys.exit(fails)
