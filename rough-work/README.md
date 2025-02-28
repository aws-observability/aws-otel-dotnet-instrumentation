After packaging the udp-exporter, 
```sh
cp bin/Release/*.nupkg ~/nuget-local/
```

Then add the local nuget source
```sh
dotnet nuget add source ~/nuget-local -n local
```

