Name: fingering
Version: %{app_version}
Release: 1
Summary: Rewrite aplikasi penarik presensi Fingerspot Revo
License: LicenseRef-Proprietary
BuildArch: x86_64
AutoReqProv: no
Requires: glibc >= 2.35, libstdc++, libgcc, zlib, openssl-libs
Requires: libX11, libICE, libSM, fontconfig, freetype
Recommends: libXcursor, libXrandr, libXi, mesa-libGL
%global debug_package %{nil}
%global __strip /bin/true

%description
Aplikasi desktop Avalonia/.NET untuk menarik presensi dari mesin Fingerspot
Revo melalui protokol FK, menyimpan database terenkripsi, dan mengekspor XLSX.
Runtime .NET disertakan. Data pengguna disimpan terpisah dari instalasi.

%prep
%build

%install
install -Dm755 "%{source_root}/artifacts/publish-linux-x64/Fingering" "%{buildroot}/opt/illustricious/fingering/Fingering"
install -Dm644 "%{source_root}/installer/linux/fingering.desktop" "%{buildroot}%{_datadir}/applications/fingering.desktop"
install -Dm644 "%{source_root}/src/Fingering.App/Assets/icon.png" "%{buildroot}%{_datadir}/pixmaps/fingering.png"
mkdir -p "%{buildroot}%{_bindir}"
ln -s /opt/illustricious/fingering/Fingering "%{buildroot}%{_bindir}/fingering"

%files
%dir /opt/illustricious
%dir /opt/illustricious/fingering
/opt/illustricious/fingering/Fingering
%{_bindir}/fingering
%{_datadir}/applications/fingering.desktop
%{_datadir}/pixmaps/fingering.png
