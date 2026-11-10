class CfgPatches {
    class tbrain_capture {
        name = "Tbrain Capture";
        units[] = {};
        weapons[] = {};
        requiredVersion = 2.18;
        requiredAddons[] = {"A3_Functions_F"};
    };
};

class CfgFunctions {
    class tbrain {
        class capture {
            file = "\tbrain_capture\functions";
            class init { postInit = 1; };
        };
    };
};
