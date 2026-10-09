// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

// System.Threading.Lock exists from .NET 9; earlier targets lock on a plain object. The alias lets
// every `lock` site use the dedicated type where it is available without a per-file conditional.
#if NET9_0_OR_GREATER
global using Gate = System.Threading.Lock;
#else
global using Gate = object;
#endif
