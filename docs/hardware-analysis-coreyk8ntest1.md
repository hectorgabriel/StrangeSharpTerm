# Hardware Analysis — coreyk8ntest1

- Collected: 2026-09-18 16:42 UTC
- OS: Ubuntu, Linux 6.8.0-124-generic x86_64
- Uptime at collection: 92 days, 21:33
- Load average: 0.01, 0.01, 0.00

## Summary

This host is a **VMware virtual machine** (full virtualization), not bare metal.
All devices are VMware virtual devices.

## CPU

- Model: Intel(R) Xeon(R) Silver 4110 @ 2.10GHz
- Cores: 8 (8 cores / 1 socket, 1 thread per core — no hyperthreading exposed)
- CPU op-modes: 32-bit, 64-bit
- Address sizes: 45 bits physical, 48 bits virtual
- Caches:
  - L1d: 256 KiB (8 instances)
  - L1i: 256 KiB (8 instances)
  - L2: 8 MiB (8 instances)
  - L3: 11 MiB (1 instance)
- NUMA nodes: 1 (CPUs 0–7)
- Flags include: avx, avx2, avx512f, avx512dq, avx512cd, avx512bw, avx512vl

## Memory

- Total: 23 GiB (~23.5 GB)
- Used: ~823 MiB (3%)
- Swap: 8.0 GiB, 0 B used

## Storage

- Root filesystem: 194G (LVM), 13G used, 172G available (7%)
- Virtual disk: 200G `Virtual disk` on VMware
  - `sda1` 1G
  - `sda2` 2G
  - `sda3` 196.9G → LVM `ubuntu--vg-ubuntu--lv`
- Controllers: VMware PVSCSI SCSI controller, VMware SATA AHCI controller
- CD-ROM: VMware Virtual SATA CDRW Drive (1G)

## Network

- VMware VMXNET3 Ethernet Controller

## Graphics

- VMware SVGA II Adapter

## Notes

- This is a modest single-socket VM with 8 vCPUs, 23 GiB RAM, and a 200G virtual disk.
- Resource utilization at collection time was very low (3% memory, ~7% disk, negligible load).
- Being VMware-backed, "physical" details such as DIMM part numbers, exact chassis,
  and RAID topology are not visible from inside the guest.
