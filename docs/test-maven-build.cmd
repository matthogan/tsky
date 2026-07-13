@echo off
setlocal EnableExtensions EnableDelayedExpansion

echo [INFO] Scanning for projects...
timeout /t 1 /nobreak >nul
echo [INFO] ------------------------------------------------------------------------
echo [INFO] Reactor Build Order:
echo [INFO]
echo [INFO] parent
echo [INFO] common-utils
echo [INFO] data-access
echo [INFO] service-api
echo [INFO] service-impl
echo [INFO] web-app
timeout /t 1 /nobreak >nul

set MODULES=parent common-utils data-access service-api service-impl web-app
for %%M in (%MODULES%) do (
    echo [INFO]
    echo [INFO] ----------------------^< com.yocto:%%M ^>-----------------------
    echo [INFO] Building %%M 1.4.2-SNAPSHOT
    echo [INFO] --------------------------------[ jar ]---------------------------------
    timeout /t 1 /nobreak >nul

    echo [INFO] --- maven-resources-plugin:3.3.1:resources ^(default-resources^) @ %%M ---
    echo [INFO] Using 'UTF-8' encoding to copy filtered resources.
    echo [INFO] Copying 5 resources
    timeout /t 1 /nobreak >nul

    echo [INFO] --- maven-compiler-plugin:3.11.0:compile ^(default-compile^) @ %%M ---
    for /L %%I in (1,1,22) do (
        echo [INFO] Compiling source %%I/22 for %%M
        if %%I==7 echo [WARNING] %%M: deprecation warning in src/main/java/com/yocto/App%%I.java
        if %%I==16 echo [WARNING] %%M: unchecked conversion in src/main/java/com/yocto/Mapper%%I.java
        timeout /t 1 /nobreak >nul
    )

    echo [INFO] --- maven-resources-plugin:3.3.1:testResources ^(default-testResources^) @ %%M ---
    echo [INFO] Copying 3 resources
    timeout /t 1 /nobreak >nul

    echo [INFO] --- maven-surefire-plugin:3.2.5:test ^(default-test^) @ %%M ---
    for /L %%T in (1,1,12) do (
        echo [INFO] Running com.yocto.%%M.TestSuite%%T
        if %%T==5 echo [INFO] Tests run: 48, Failures: 0, Errors: 0, Skipped: 1
        timeout /t 1 /nobreak >nul
    )

    echo [INFO] --- maven-jar-plugin:3.4.2:jar ^(default-jar^) @ %%M ---
    echo [INFO] Building jar: target\%%M-1.4.2-SNAPSHOT.jar
    timeout /t 1 /nobreak >nul

    echo [INFO] BUILD MODULE SUCCESS: %%M
)

echo [INFO] ------------------------------------------------------------------------
echo [INFO] Reactor Summary for yocto-parent 1.4.2-SNAPSHOT:
echo [INFO]
echo [INFO] parent ............... SUCCESS [ 12.440 s]
echo [INFO] common-utils ......... SUCCESS [ 48.213 s]
echo [INFO] data-access .......... SUCCESS [ 41.902 s]
echo [INFO] service-api .......... SUCCESS [ 39.772 s]
echo [INFO] service-impl ......... SUCCESS [ 52.018 s]
echo [INFO] web-app .............. SUCCESS [ 64.110 s]
echo [INFO] ------------------------------------------------------------------------
echo [INFO] BUILD SUCCESS
echo [INFO] ------------------------------------------------------------------------
echo [INFO] Total time:  04:38 min
echo [INFO] Finished at: %DATE% %TIME%
echo [INFO] ------------------------------------------------------------------------

endlocal
