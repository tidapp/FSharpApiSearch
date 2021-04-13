#!/bin/bash

: ${projectdir:=~/projects/tidapp/cloud/fn/build/homepage}
: ${nugetdir:=~/.nuget/packages}

libs=$(
    find "$projectdir" -iname '*.fsproj' -exec grep -iF '<PackageReference' {} \; |
    sed -e 's:.*Include="\([^"]\+\)".*:\1.dll:i' |
    sort -u
)

mkdir -p lib/
find "$nugetdir" | grep -iFf <( echo "$libs" ) | sort -n | xargs -I{} -r cp {} lib/

if uname -a | grep -qi microsoft; then
    exec ./bin/FSharpApiSearch.Database/net5.0/FSharpApiSearch.Database.exe "--FSharpCore:C:/Program Files/dotnet/sdk/5.0.201/FSharp" "--Framework:C:/Program Files/dotnet/shared/Microsoft.AspNetCore.App/5.0.4/" "--Framework:C:/Program Files/dotnet/shared/Microsoft.NETCore.App/5.0.4/" --lib:lib/ mscorlib FSharp.Core $(for i in lib/*.dll; do basename $i; done)
else
    exec ./bin/FSharpApiSearch.Database/net5.0/FSharpApiSearch.Database "--FSharpCore:/usr/share/dotnet/sdk/5.0.201/FSharp/" "--Framework:/usr/share/dotnet/shared/Microsoft.NETCore.App/5.0.4/" "--Framework:/usr/share/dotnet/shared/Microsoft.AspNetCore.App/5.0.4/" --lib:lib/ mscorlib FSharp.Core $(for i in lib/*.dll; do basename $i; done)
fi
