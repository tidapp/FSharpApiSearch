#!/bin/bash

mkdir -p lib/
#find /mnt/c/Users/rpetrano/projects/cloud/fn/build/homepage/ -iname '*.dll' -exec cp '{}' lib/ \;

./bin/FSharpApiSearch.Database/net5.0/FSharpApiSearch.Database.exe "--FSharpCore:C:/Program Files/dotnet/sdk/5.0.201/FSharp" "--Framework:C:/Program Files/dotnet/shared/Microsoft.AspNetCore.App/5.0.4/" "--Framework:C:/Program Files/dotnet/shared/Microsoft.NETCore.App/5.0.4/" --lib:lib/ mscorlib FSharp.Core $(for i in lib/*.dll; do basename $i; done)
