import SwiftUI

// MARK: - Color helpers

extension Color {
    init(hex: String) {
        let s = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        var v: UInt64 = 0
        Scanner(string: s).scanHexInt64(&v)
        let r, g, b, a: Double
        switch s.count {
        case 8:
            r = Double((v >> 24) & 0xFF) / 255
            g = Double((v >> 16) & 0xFF) / 255
            b = Double((v >> 8) & 0xFF) / 255
            a = Double(v & 0xFF) / 255
        default:
            r = Double((v >> 16) & 0xFF) / 255
            g = Double((v >> 8) & 0xFF) / 255
            b = Double(v & 0xFF) / 255
            a = 1
        }
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }

    static func rgba(_ r: Double, _ g: Double, _ b: Double, _ a: Double) -> Color {
        Color(.sRGB, red: r / 255, green: g / 255, blue: b / 255, opacity: a)
    }
}

// MARK: - Palette (Gmail-inspired light + dark token sets)

struct Palette {
    // Brand
    let brandBlue: Color
    let brandBlue600: Color
    let brandBlue700: Color
    let brandBlue100: Color
    let brandBlue50: Color
    let magenta: Color
    let magenta600: Color
    let magenta100: Color

    // Neutral text
    let fg1: Color
    let fg2: Color
    let fg3: Color
    let fg4: Color

    // Surfaces
    let bg1: Color
    let bg2: Color
    let bg3: Color
    let bg4: Color

    // Lines
    let border: Color
    let borderStrong: Color

    // Semantic
    let success: Color
    let success100: Color
    let warning: Color
    let warning100: Color
    let danger: Color
    let danger100: Color

    // For folder-active text in dark mode
    let activeFolderText: Color

    // Gmail surfaces
    let composeBg: Color      // pastel "Compose" button
    let composeFg: Color
    let searchBg: Color       // pill search field
    let readRow: Color        // tint behind already-read rows
    let star: Color

    let isDark: Bool

    // Gmail (Material 3) light tokens.
    static let light = Palette(
        brandBlue: Color(hex: "0B57D0"),
        brandBlue600: Color(hex: "0842A0"),
        brandBlue700: Color(hex: "062E6F"),
        brandBlue100: Color(hex: "D3E3FD"),
        brandBlue50: Color(hex: "E8F0FE"),
        magenta: Color(hex: "D93025"),
        magenta600: Color(hex: "B3261E"),
        magenta100: Color(hex: "FCE8E6"),
        fg1: Color(hex: "1F1F1F"),
        fg2: Color(hex: "444746"),
        fg3: Color(hex: "5F6368"),
        fg4: Color(hex: "80868B"),
        bg1: Color(hex: "FFFFFF"),
        bg2: Color(hex: "F6F8FC"),
        bg3: Color(hex: "EEF2F9"),
        bg4: Color(hex: "E1E5EA"),
        border: .rgba(31, 31, 31, 0.08),
        borderStrong: .rgba(31, 31, 31, 0.16),
        success: Color(hex: "188038"),
        success100: Color(hex: "E6F4EA"),
        warning: Color(hex: "F4B400"),
        warning100: Color(hex: "FEF7E0"),
        danger: Color(hex: "D93025"),
        danger100: Color(hex: "FCE8E6"),
        activeFolderText: Color(hex: "001D35"),
        composeBg: Color(hex: "C2E7FF"),
        composeFg: Color(hex: "001D35"),
        searchBg: Color(hex: "E9EEF6"),
        readRow: Color(hex: "F2F6FC"),
        star: Color(hex: "F4B400"),
        isDark: false
    )

    // Gmail dark tokens.
    static let dark = Palette(
        brandBlue: Color(hex: "4C8DF6"),
        brandBlue600: Color(hex: "3B7AE0"),
        brandBlue700: Color(hex: "2B66C6"),
        brandBlue100: Color(hex: "004A77"),
        brandBlue50: Color(hex: "0B2A44"),
        magenta: Color(hex: "F28B82"),
        magenta600: Color(hex: "EE675C"),
        magenta100: .rgba(242, 139, 130, 0.16),
        fg1: Color(hex: "E3E3E3"),
        fg2: Color(hex: "C4C7C5"),
        fg3: Color(hex: "A8ABAA"),
        fg4: Color(hex: "8E918F"),
        bg1: Color(hex: "1E1F20"),
        bg2: Color(hex: "131314"),
        bg3: Color(hex: "282A2C"),
        bg4: Color(hex: "333537"),
        border: .rgba(255, 255, 255, 0.08),
        borderStrong: .rgba(255, 255, 255, 0.16),
        success: Color(hex: "81C995"),
        success100: .rgba(129, 201, 149, 0.16),
        warning: Color(hex: "FDD663"),
        warning100: .rgba(253, 214, 99, 0.16),
        danger: Color(hex: "F28B82"),
        danger100: .rgba(242, 139, 130, 0.16),
        activeFolderText: Color(hex: "C2E7FF"),
        composeBg: Color(hex: "004A77"),
        composeFg: Color(hex: "C2E7FF"),
        searchBg: Color(hex: "282A2C"),
        readRow: Color(hex: "1A1B1C"),
        star: Color(hex: "FDD663"),
        isDark: true
    )
}

private struct PaletteKey: EnvironmentKey {
    static let defaultValue: Palette = .light
}

extension EnvironmentValues {
    var palette: Palette {
        get { self[PaletteKey.self] }
        set { self[PaletteKey.self] = newValue }
    }
}

// MARK: - Typography scale (native SF Pro; design used Plus Jakarta Sans)

enum Typo {
    static func display() -> Font { .system(size: 64, weight: .heavy) }
    static func h1() -> Font { .system(size: 48, weight: .heavy) }
    static func h2() -> Font { .system(size: 36, weight: .bold) }
    static func h3() -> Font { .system(size: 30, weight: .bold) }
    static func mono(_ size: CGFloat) -> Font { .system(size: size, design: .monospaced) }
}
