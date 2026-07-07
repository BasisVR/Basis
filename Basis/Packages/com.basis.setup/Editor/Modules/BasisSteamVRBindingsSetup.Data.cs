namespace Basis.Setup.Modules
{
    public sealed partial class BasisSteamVRBindingsSetup
    {
        internal static readonly (string Name, string Content)[] Files =
        {
            ("actions.json", @"{
  ""actions"": [
    {
      ""name"": ""/actions/default/in/Pose"",
      ""type"": ""pose""
    },
    {
      ""name"": ""/actions/default/in/EyeGaze"",
      ""type"": ""pose"",
      ""requirement"": ""optional""
    },
    {
      ""name"": ""/actions/default/in/SkeletonLeftHand"",
      ""type"": ""skeleton"",
      ""skeleton"": ""/skeleton/hand/left"",
      ""requirement"": ""mandatory""
    },
    {
      ""name"": ""/actions/default/in/SkeletonRightHand"",
      ""type"": ""skeleton"",
      ""skeleton"": ""/skeleton/hand/right"",
      ""requirement"": ""mandatory""
    },
    {
      ""name"": ""/actions/default/in/Joystick"",
      ""type"": ""vector2"",
      ""requirement"": ""suggested""
    },
    {
      ""name"": ""/actions/default/in/B-Button"",
      ""type"": ""boolean""
    },
    {
      ""name"": ""/actions/default/in/A_Button"",
      ""type"": ""boolean""
    },
    {
      ""name"": ""/actions/default/in/Trigger"",
      ""type"": ""vector1""
    },
    {
      ""name"": ""/actions/default/in/JoyStickClick"",
      ""type"": ""boolean""
    },
    {
      ""name"": ""/actions/default/in/TrackPad"",
      ""type"": ""vector2""
    },
    {
      ""name"": ""/actions/default/in/System"",
      ""type"": ""boolean""
    },
    {
      ""name"": ""/actions/default/in/HandTrigger"",
      ""type"": ""vector1""
    },
    {
      ""name"": ""/actions/default/in/TrackPadTouched"",
      ""type"": ""boolean""
    },
    {
      ""name"": ""/actions/default/in/Grip"",
      ""type"": ""boolean""
    },
    {
      ""name"": ""/actions/default/out/Haptic"",
      ""type"": ""vibration""
    }
  ],
  ""action_sets"": [
    {
      ""name"": ""/actions/default"",
      ""usage"": ""single""
    }
  ],
  ""default_bindings"": [
    {
      ""controller_type"": ""vive_controller"",
      ""binding_url"": ""bindings_vive_controller.json""
    },
    {
      ""controller_type"": ""oculus_touch"",
      ""binding_url"": ""bindings_oculus_touch.json""
    },
    {
      ""controller_type"": ""knuckles"",
      ""binding_url"": ""bindings_knuckles.json""
    },
    {
      ""controller_type"": ""holographic_controller"",
      ""binding_url"": ""bindings_holographic_controller.json""
    },
    {
      ""controller_type"": ""vive_cosmos_controller"",
      ""binding_url"": ""bindings_vive_cosmos_controller.json""
    },
    {
      ""controller_type"": ""logitech_stylus"",
      ""binding_url"": ""bindings_logitech_stylus.json""
    },
    {
      ""controller_type"": ""vive_cosmos"",
      ""binding_url"": ""binding_vive_cosmos.json""
    },
    {
      ""controller_type"": ""vive"",
      ""binding_url"": ""binding_vive.json""
    },
    {
      ""controller_type"": ""indexhmd"",
      ""binding_url"": ""binding_index_hmd.json""
    },
    {
      ""controller_type"": ""vive_pro"",
      ""binding_url"": ""binding_vive_pro.json""
    },
    {
      ""controller_type"": ""rift"",
      ""binding_url"": ""binding_rift.json""
    },
    {
      ""controller_type"": ""holographic_hmd"",
      ""binding_url"": ""binding_holographic_hmd.json""
    },
    {
      ""controller_type"": ""vive_tracker_camera"",
      ""binding_url"": ""binding_vive_tracker_camera.json""
    }
  ],
  ""localization"": [
    {
      ""language_tag"": ""en_US"",
      ""/actions/default/in/Pose"": ""Pose"",
      ""/actions/default/in/EyeGaze"": ""Eye Gaze"",
      ""/actions/default/in/SkeletonLeftHand"": ""Skeleton (Left)"",
      ""/actions/default/in/SkeletonRightHand"": ""Skeleton (Right)"",
      ""/actions/default/out/Haptic"": ""Haptic""
    }
  ]
}"),
            ("binding_holographic_hmd.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        }
      ],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""holographic_hmd"",
  ""description"": """",
  ""name"": ""holographic_hmd defaults""
}"),
            ("binding_index_hmd.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        }
      ],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""indexhmd"",
  ""description"": """",
  ""name"": ""index hmd defaults""
}"),
            ("binding_rift.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        }
      ],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""rift"",
  ""description"": """",
  ""name"": ""rift defaults""
}"),
            ("binding_vive.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/head/pose/raw""
        }
      ],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""vive"",
  ""description"": """",
  ""name"": ""vive defaults""
}"),
            ("binding_vive_cosmos.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""vive_cosmos"",
  ""description"": """",
  ""name"": ""vive cosmos hmd defaults""
}"),
            ("binding_vive_pro.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""vive_pro"",
  ""description"": """",
  ""name"": ""vive_pro defaults""
}"),
            ("binding_vive_tracker_camera.json", @"{
  ""bindings"": {
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/camera/pose/raw""
        }
      ],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/mixedreality"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""vive_tracker_camera"",
  ""description"": """",
  ""name"": ""Holodance / Beat the Rhythm config for Mixed Reality Camera""
}"),
            ("bindings_holographic_controller.json", @"{
  ""bindings"": {
    ""/actions/buggy"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/left/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/right/pose/raw""
        }
      ],
      ""haptics"": [
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/left/output/haptic""
        },
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/right/output/haptic""
        }
      ],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/platformer"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""holographic_controller"",
  ""description"": """",
  ""name"": ""Default bindings for Windows Mixed Reality Controllers""
}"),
            ("bindings_knuckles.json", @"{
   ""action_manifest_version"" : 0,
   ""alias_info"" : {},
   ""app_key"" : ""application.generated.unity.basisunity.exe"",
   ""bindings"" : {
      ""/actions/buggy"" : {
         ""chords"" : [],
         ""haptics"" : [],
         ""poses"" : [],
         ""skeleton"" : [],
         ""sources"" : []
      },
      ""/actions/default"" : {
         ""chords"" : [],
         ""haptics"" : [
            {
               ""output"" : ""/actions/default/out/haptic"",
               ""path"" : ""/user/hand/left/output/haptic""
            },
            {
               ""output"" : ""/actions/default/out/haptic"",
               ""path"" : ""/user/hand/right/output/haptic""
            }
         ],
         ""poses"" : [
            {
               ""output"" : ""/actions/default/in/pose"",
               ""path"" : ""/user/hand/left/pose/raw""
            },
            {
               ""output"" : ""/actions/default/in/pose"",
               ""path"" : ""/user/hand/right/pose/raw""
            }
         ],
         ""skeleton"" : [
            {
               ""output"" : ""/actions/default/in/skeletonlefthand"",
               ""path"" : ""/user/hand/left/input/skeleton/left""
            },
            {
               ""output"" : ""/actions/default/in/skeletonrighthand"",
               ""path"" : ""/user/hand/right/input/skeleton/right""
            }
         ],
         ""sources"" : [
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/joystickclick""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/default/in/joystick""
                  }
               },
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/left/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/b-button""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/b""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/default/in/trigger""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/left/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/a_button""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/a""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/default/in/handtrigger""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/left/input/grip""
            },
            {
               ""inputs"" : {
                  ""position"" : {
                     ""output"" : ""/actions/default/in/trackpad""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/default/in/trackpadtouched""
                  }
               },
               ""mode"" : ""trackpad"",
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/system""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/system""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/a_button""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/a""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/b-button""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/b""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/default/in/handtrigger""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/right/input/grip""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/system""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/system""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/joystickclick""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/default/in/joystick""
                  }
               },
               ""mode"" : ""joystick"",
               ""path"" : ""/user/hand/right/input/thumbstick""
            },
            {
               ""inputs"" : {
                  ""position"" : {
                     ""output"" : ""/actions/default/in/trackpad""
                  },
                  ""touch"" : {
                     ""output"" : ""/actions/default/in/trackpadtouched""
                  }
               },
               ""mode"" : ""trackpad"",
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/default/in/trigger""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/right/input/trigger""
            },
            {
               ""inputs"" : {
                  ""grab"" : {
                     ""output"" : ""/actions/default/in/grip""
                  }
               },
               ""mode"" : ""grab"",
               ""parameters"" : {
                  ""force_hold_threshold"" : ""0.15"",
                  ""value_hold_threshold"" : ""1.1"",
                  ""value_release_threshold"" : ""0.65""
               },
               ""path"" : ""/user/hand/left/input/grip""
            },
            {
               ""inputs"" : {
                  ""grab"" : {
                     ""output"" : ""/actions/default/in/grip""
                  }
               },
               ""mode"" : ""grab"",
               ""parameters"" : {
                  ""force_hold_threshold"" : ""0.15"",
                  ""value_hold_threshold"" : ""1.1"",
                  ""value_release_threshold"" : ""0.65""
               },
               ""path"" : ""/user/hand/right/input/grip""
            }
         ]
      },
      ""/actions/platformer"" : {
         ""chords"" : [],
         ""haptics"" : [],
         ""poses"" : [],
         ""skeleton"" : [],
         ""sources"" : []
      }
   },
   ""category"" : ""steamvr_input"",
   ""controller_type"" : ""knuckles"",
   ""description"" : """",
   ""interaction_profile"" : """",
   ""name"" : ""Basis configuration for Index Controller"",
   ""options"" : {},
   ""simulated_actions"" : []
}
"),
            ("bindings_logitech_stylus.json", @"{
  ""bindings"": {
    ""/actions/buggy"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/left/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/right/pose/raw""
        }
      ],
      ""haptics"": [
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/left/output/haptic""
        },
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/right/output/haptic""
        }
      ],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/platformer"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""vive_controller"",
  ""description"": """",
  ""name"": ""vive_controller""
}"),
            ("bindings_oculus_touch.json", @"{
  ""app_key"": ""application.generated.unity.basisunity.exe"",
  ""bindings"": {
    ""/actions/buggy"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/left/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/right/pose/raw""
        }
      ],
      ""haptics"": [
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/left/output/haptic""
        },
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/right/output/haptic""
        }
      ],
      ""sources"": [
        {
          ""path"": ""/user/hand/left/input/thumbstick"",
          ""mode"": ""joystick"",
          ""parameters"": {},
          ""inputs"": {
            ""position"": {
              ""output"": ""/actions/default/in/joystick""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/thumbstick"",
          ""mode"": ""joystick"",
          ""parameters"": {},
          ""inputs"": {
            ""position"": {
              ""output"": ""/actions/default/in/joystick""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/b"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/b-button""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/trigger"",
          ""mode"": ""trigger"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/trigger""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/trigger"",
          ""mode"": ""trigger"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/trigger""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/a"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/a_button""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/joystick"",
          ""mode"": ""joystick"",
          ""parameters"": {},
          ""inputs"": {
            ""position"": {
              ""output"": ""/actions/default/in/joystick""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/a"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/a_button""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/b"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/b-button""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/joystick"",
          ""mode"": ""joystick"",
          ""parameters"": {},
          ""inputs"": {
            ""position"": {
              ""output"": ""/actions/default/in/joystick""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/x"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/a_button""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/x"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/a_button""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/y"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/b-button""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/y"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/b-button""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/grip"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/grip""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/grip"",
          ""mode"": ""button"",
          ""parameters"": {},
          ""inputs"": {
            ""click"": {
              ""output"": ""/actions/default/in/grip""
            }
          }
        },
        {
          ""path"": ""/user/hand/left/input/grip"",
          ""mode"": ""trigger"",
          ""parameters"": {},
          ""inputs"": {
            ""pull"": {
              ""output"": ""/actions/default/in/handtrigger""
            }
          }
        },
        {
          ""path"": ""/user/hand/right/input/grip"",
          ""mode"": ""trigger"",
          ""parameters"": {},
          ""inputs"": {
            ""pull"": {
              ""output"": ""/actions/default/in/handtrigger""
            }
          }
        }
      ],
      ""skeleton"": [
        {
          ""output"": ""/actions/default/in/skeletonlefthand"",
          ""path"": ""/user/hand/left/input/skeleton/left""
        },
        {
          ""output"": ""/actions/default/in/skeletonrighthand"",
          ""path"": ""/user/hand/right/input/skeleton/right""
        }
      ]
    },
    ""/actions/platformer"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""oculus_touch"",
  ""description"": """",
  ""name"": ""Basis configuration for Oculus Touch Controller""
}"),
            ("bindings_vive_controller.json", @"{
   ""action_manifest_version"" : 0,
   ""alias_info"" : {},
   ""app_key"" : ""application.generated.unity.basisunity.exe"",
   ""bindings"" : {
      ""/actions/buggy"" : {
         ""chords"" : [],
         ""haptics"" : [],
         ""poses"" : [],
         ""skeleton"" : [],
         ""sources"" : []
      },
      ""/actions/default"" : {
         ""chords"" : [],
         ""haptics"" : [
            {
               ""output"" : ""/actions/default/out/haptic"",
               ""path"" : ""/user/hand/left/output/haptic""
            },
            {
               ""output"" : ""/actions/default/out/haptic"",
               ""path"" : ""/user/hand/right/output/haptic""
            }
         ],
         ""poses"" : [
            {
               ""output"" : ""/actions/default/in/pose"",
               ""path"" : ""/user/hand/left/pose/raw""
            },
            {
               ""output"" : ""/actions/default/in/pose"",
               ""path"" : ""/user/hand/right/pose/raw""
            }
         ],
         ""skeleton"" : [
            {
               ""output"" : ""/actions/default/in/skeletonlefthand"",
               ""path"" : ""/user/hand/left/input/skeleton/left""
            },
            {
               ""output"" : ""/actions/default/in/skeletonrighthand"",
               ""path"" : ""/user/hand/right/input/skeleton/right""
            }
         ],
         ""sources"" : [
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/grip""
                  }
               },
               ""mode"" : ""toggle_button"",
               ""path"" : ""/user/hand/left/input/grip""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/grip""
                  }
               },
               ""mode"" : ""toggle_button"",
               ""path"" : ""/user/hand/right/input/grip""
            },
            {
               ""inputs"" : {
                  ""value"" : {
                     ""output"" : ""/actions/default/in/handtrigger""
                  }
               },
               ""mode"" : ""scalar_constant"",
               ""path"" : ""/user/hand/left/input/grip""
            },
            {
               ""inputs"" : {
                  ""value"" : {
                     ""output"" : ""/actions/default/in/handtrigger""
                  }
               },
               ""mode"" : ""scalar_constant"",
               ""path"" : ""/user/hand/right/input/grip""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/b-button""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/application_menu""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/b-button""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/application_menu""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/default/in/trigger""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/left/input/trigger""
            },
            {
               ""inputs"" : {
                  ""pull"" : {
                     ""output"" : ""/actions/default/in/trigger""
                  }
               },
               ""mode"" : ""trigger"",
               ""path"" : ""/user/hand/right/input/trigger""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/joystickclick""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/default/in/joystick""
                  }
               },
               ""mode"" : ""trackpad"",
               ""parameters"" : {
                  ""deadzone_pct"" : ""25"",
                  ""maxzone_pct"" : ""90""
               },
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/joystickclick""
                  },
                  ""position"" : {
                     ""output"" : ""/actions/default/in/joystick""
                  }
               },
               ""mode"" : ""trackpad"",
               ""parameters"" : {
                  ""deadzone_pct"" : ""25"",
                  ""maxzone_pct"" : ""90""
               },
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""center"" : {
                     ""output"" : ""/actions/default/in/a_button""
                  }
               },
               ""mode"" : ""dpad"",
               ""parameters"" : {
                  ""sub_mode"" : ""click""
               },
               ""path"" : ""/user/hand/left/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""center"" : {
                     ""output"" : ""/actions/default/in/a_button""
                  }
               },
               ""mode"" : ""dpad"",
               ""parameters"" : {
                  ""sub_mode"" : ""click""
               },
               ""path"" : ""/user/hand/right/input/trackpad""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/system""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/left/input/system""
            },
            {
               ""inputs"" : {
                  ""click"" : {
                     ""output"" : ""/actions/default/in/system""
                  }
               },
               ""mode"" : ""button"",
               ""path"" : ""/user/hand/right/input/system""
            }
         ]
      },
      ""/actions/platformer"" : {
         ""chords"" : [],
         ""haptics"" : [],
         ""poses"" : [],
         ""skeleton"" : [],
         ""sources"" : []
      }
   },
   ""category"" : ""steamvr_input"",
   ""controller_type"" : ""vive_controller"",
   ""description"" : """",
   ""interaction_profile"" : """",
   ""name"" : ""Basis configuration for Vive Controller"",
   ""options"" : {},
   ""simulated_actions"" : []
}
"),
            ("bindings_vive_cosmos_controller.json", @"{
  ""bindings"": {
    ""/actions/buggy"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    },
    ""/actions/default"": {
      ""chords"": [],
      ""poses"": [
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/left/pose/raw""
        },
        {
          ""output"": ""/actions/default/in/pose"",
          ""path"": ""/user/hand/right/pose/raw""
        }
      ],
      ""haptics"": [
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/left/output/haptic""
        },
        {
          ""output"": ""/actions/default/out/haptic"",
          ""path"": ""/user/hand/right/output/haptic""
        }
      ],
      ""sources"": [],
      ""skeleton"": [
        {
          ""output"": ""/actions/default/in/skeletonlefthand"",
          ""path"": ""/user/hand/left/input/skeleton/left""
        },
        {
          ""output"": ""/actions/default/in/skeletonrighthand"",
          ""path"": ""/user/hand/right/input/skeleton/right""
        }
      ]
    },
    ""/actions/platformer"": {
      ""chords"": [],
      ""poses"": [],
      ""haptics"": [],
      ""sources"": [],
      ""skeleton"": []
    }
  },
  ""controller_type"": ""vive_cosmos_controller"",
  ""description"": """",
  ""name"": ""vive_cosmos_controller""
}"),
        };
    }
}
