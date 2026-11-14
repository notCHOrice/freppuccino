# The PLAN

# Notes

- Project name is Freppuccino

# The Goal

We're working on adding support for implicit geometry modelling in Rhino 9.
Users should be able to model in real time and see changes to, and any transformations of the objects represented as F-Rep immediately. The user will edit the geometry using the gum-ball. Users edit rhino objects as they would normally, with the gum-ball and commands.

This project is for industrial designers, aerospace, boolean and fillet aficionados.

# High level Architecture

- Rhino plugin
- Custom commands for each geometry type (FBox, FSphere, etc.)
- F-reps are normal rhino (geometry) objects such as a Brep, Surface etc. that contain parameter metadata created during the commands, a cone would have a base diameter and a height, a sphere a diameter.
- All F-rep geometry types have a function defining them

# Technical details
- Users switch on a F-rep display mode
  - The display mode renders to the GPU using a shader (An F-rep pipeline)
    - The reason for this is the technical constraints of Rhino, i.e we cannot add a new geometry type that would work fast enough.

# Features
- Command to convert F-reps to mesh
- Commands to create F-rep objects


# Glossary

- F-rep -> Function-REPresentation
