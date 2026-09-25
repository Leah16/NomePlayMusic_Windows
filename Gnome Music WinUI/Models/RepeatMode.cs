// SPDX-License-Identifier: GPL-2.0-or-later
namespace Gnome_Music_WinUI.Models;

/// <summary>
/// Playback repeat mode (utils.py: RepeatMode). SHUFFLE shuffles the queue and
/// implies repeating all of it.
/// </summary>
public enum RepeatMode
{
    None = 0,
    Song = 1,
    All = 2,
    Shuffle = 3,
}
